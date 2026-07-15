using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UiAssemblerSlice.Editor.Assembler;
using UiAssemblerSlice.Editor.Batch;
using UnityEditor;
using UnityEngine;

namespace UiAssemblerSlice.Editor.Review
{
    /// Stage 3: runs the remaining Node-side pipeline (reduce-from-selection
    /// -> match) against a Figma plugin export, shows one review row per
    /// match-result.json entry (accept/reject/reassign), and assembles the
    /// final prefab in-process - the same three calls RunAssemble.cs's
    /// batchmode entry point makes, just triggered live instead of headless.
    ///
    /// Known limitation, not fixed here: none of this window's state
    /// survives a domain reload (e.g. editing a C# script while the window
    /// is open) - all fields are plain `private`, deliberately not
    /// [SerializeField], since a Dictionary/Texture2D/PipelineRunner aren't
    /// things Unity's serializer should be attempting to persist. Re-run
    /// "Load match-result.json for review" after a reload if needed.
    public class ReviewWindow : EditorWindow
    {
        [MenuItem("UI Assembler/Review Window")]
        public static void Open()
        {
            GetWindow<ReviewWindow>("UI Assembler Review");
        }

        // ---- user-entered inputs ----
        private string _pluginExportPath = "";
        private string _catalogPath = ".cache/catalog.json";
        // GUI-launched Unity on macOS doesn't source shell rc files, so a
        // nvm-installed npm can be invisible to a bare "npm" even though it
        // works fine from a terminal - override here if "Run pipeline"
        // fails to start with a "cannot find the specified file" error.
        private string _npmPath = "npm";

        // ---- pipeline ----
        private readonly PipelineRunner _pipeline = new PipelineRunner();
        private readonly List<string> _logLines = new List<string>();
        private Vector2 _logScroll;
        private string _statusMessage = "";

        // ---- loaded review data ----
        private ElementTreeData _elementTree;
        private List<MatchResultEntry> _matchResults;
        private List<CatalogEntryData> _catalog;
        private Dictionary<string, string> _elementThumbnails = new Dictionary<string, string>();
        private Dictionary<string, ElementData> _elementById = new Dictionary<string, ElementData>();
        private Dictionary<string, CatalogEntryData> _catalogById = new Dictionary<string, CatalogEntryData>();
        private Vector2 _reviewScroll;

        // ---- preview textures - native-backed, must be explicitly destroyed ----
        private readonly Dictionary<string, Texture2D> _textureCache = new Dictionary<string, Texture2D>();

        // Same traversal RunAssemble.cs/RunCatalogBuild.cs already use:
        // Application.dataPath is Melon/Assets, and Melon + this repo are
        // sibling folders under the same parent.
        private static string RepoRoot =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "unity-ui-assembly-tool"));

        // Fixed .cache/-relative outputs, not user-entered - matches
        // cli.ts's own defaults exactly, so pointing the pipeline steps at
        // these explicit output paths and reading them back afterward is
        // guaranteed consistent.
        private string ElementTreePath => Path.Combine(RepoRoot, ".cache", "element-tree.json");
        private string MatchResultPath => Path.Combine(RepoRoot, ".cache", "match-result.json");
        private string ElementThumbnailsPath => Path.Combine(RepoRoot, ".cache", "element-thumbnails.json");

        private static string ResolvePath(string userPath, string repoRoot)
        {
            if (string.IsNullOrWhiteSpace(userPath)) return null;
            return Path.IsPathRooted(userPath) ? userPath : Path.GetFullPath(Path.Combine(repoRoot, userPath));
        }

        private void OnDestroy()
        {
            _pipeline.Stop();
            ClearTextureCache();
        }

        private void OnGUI()
        {
            DrawInputs();
            EditorGUILayout.Space();
            DrawPipelineControls();
            EditorGUILayout.Space();

            if (!string.IsNullOrEmpty(_statusMessage))
            {
                EditorGUILayout.HelpBox(_statusMessage, MessageType.Info);
                EditorGUILayout.Space();
            }

            if (_matchResults != null)
            {
                DrawReviewList();
                EditorGUILayout.Space();
                DrawAssembleControls();
            }
        }

        private void DrawInputs()
        {
            EditorGUILayout.LabelField("Inputs", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            _pluginExportPath = EditorGUILayout.TextField("Plugin export JSON", _pluginExportPath);
            if (GUILayout.Button("Browse...", GUILayout.Width(70)))
            {
                var picked = EditorUtility.OpenFilePanel("Select Figma plugin export", "", "json");
                if (!string.IsNullOrEmpty(picked)) _pluginExportPath = picked;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            _catalogPath = EditorGUILayout.TextField("Catalog JSON", _catalogPath);
            if (GUILayout.Button("Browse...", GUILayout.Width(70)))
            {
                var picked = EditorUtility.OpenFilePanel("Select catalog.json", "", "json");
                if (!string.IsNullOrEmpty(picked)) _catalogPath = picked;
            }
            EditorGUILayout.EndHorizontal();

            _npmPath = EditorGUILayout.TextField("npm path", _npmPath);
            EditorGUILayout.LabelField("-> " + ElementTreePath, EditorStyles.miniLabel);
            EditorGUILayout.LabelField("-> " + MatchResultPath, EditorStyles.miniLabel);
            EditorGUILayout.LabelField("-> " + ElementThumbnailsPath, EditorStyles.miniLabel);
        }

        private void DrawPipelineControls()
        {
            // Deliberately NOT gated on _pluginExportPath being non-empty -
            // that used to silently disable the button with no feedback at
            // all (confusing - "the button is locked"). Always clickable
            // while not running; RunPipeline() itself reports a clear
            // _statusMessage if the path is missing, same as any other
            // input-validation error in this window.
            GUI.enabled = !_pipeline.IsRunning;
            if (GUILayout.Button("Run pipeline (reduce-from-selection -> match)"))
            {
                RunPipeline();
            }

            if (GUILayout.Button("Load match-result.json for review"))
            {
                LoadReviewData();
            }
            GUI.enabled = true;

            if (string.IsNullOrWhiteSpace(_pluginExportPath))
            {
                EditorGUILayout.HelpBox("Set a plugin export JSON path (or Browse...) to run the pipeline.", MessageType.None);
            }

            if (_logLines.Count > 0)
            {
                _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUILayout.Height(120));
                EditorGUILayout.TextArea(string.Join("\n", _logLines), GUILayout.ExpandHeight(true));
                EditorGUILayout.EndScrollView();
            }
        }

        private void RunPipeline()
        {
            _logLines.Clear();
            var repoRoot = RepoRoot;
            var pluginExportAbs = ResolvePath(_pluginExportPath, repoRoot);
            var catalogAbs = ResolvePath(_catalogPath, repoRoot) ?? Path.Combine(repoRoot, ".cache", "catalog.json");

            if (pluginExportAbs == null)
            {
                _statusMessage = "Set a plugin export JSON path first.";
                return;
            }

            var steps = new List<(string, string, string)>
            {
                (_npmPath,
                    $"run cli --workspace=@ui-assembler-slice/mcp-tool -- reduce-from-selection \"{pluginExportAbs}\" \"{ElementTreePath}\"",
                    repoRoot),
                (_npmPath,
                    $"run cli --workspace=@ui-assembler-slice/mcp-tool -- match \"{ElementTreePath}\" \"{catalogAbs}\" \"{MatchResultPath}\"",
                    repoRoot),
            };

            _statusMessage = "Running pipeline...";
            _pipeline.RunSteps(
                steps,
                onLogLine: line =>
                {
                    _logLines.Add(line);
                    Repaint();
                },
                onAllFinished: () =>
                {
                    _statusMessage = "Pipeline finished";
                    LoadReviewData();
                    Repaint();
                },
                onFailed: err =>
                {
                    _statusMessage = $"Pipeline failed: {err}";
                    Repaint();
                });
        }

        private void LoadReviewData()
        {
            try
            {
                var repoRoot = RepoRoot;
                var catalogAbs = ResolvePath(_catalogPath, repoRoot) ?? Path.Combine(repoRoot, ".cache", "catalog.json");

                _elementTree = AssemblerJson.LoadElementTree(ElementTreePath);
                _catalog = AssemblerJson.LoadCatalog(catalogAbs);
                _matchResults = AssemblerJson.LoadMatchResults(MatchResultPath);
                _elementThumbnails = File.Exists(ElementThumbnailsPath)
                    ? AssemblerJson.LoadElementThumbnails(ElementThumbnailsPath)
                    : new Dictionary<string, string>();

                _elementById = new Dictionary<string, ElementData>();
                FlattenElements(_elementTree.Elements, _elementById);
                _catalogById = _catalog.ToDictionary(c => c.Id);

                ClearTextureCache();
                // Thumbnail count called out explicitly, not just implied -
                // a plugin export downloaded before the thumbnail retrofit
                // (or one where nothing got captured) produces an empty
                // sidecar with NO error anywhere, which otherwise looks
                // identical to a real bug in the preview-rendering code.
                // Re-export from the Figma plugin if this reads 0 and you
                // expected previews.
                var thumbCount = _elementThumbnails.Count;
                var thumbNote = thumbCount == 0
                    ? " - 0 thumbnails loaded (element-thumbnails.json is empty or missing; re-export from the Figma plugin to get real previews)"
                    : $" - {thumbCount} thumbnails loaded";
                _statusMessage = $"Loaded {_matchResults.Count} match-result entries{thumbNote}";
            }
            catch (Exception ex)
            {
                _statusMessage = $"Load failed: {ex.Message}";
                Debug.LogException(ex);
            }
        }

        private static void FlattenElements(List<ElementData> elements, Dictionary<string, ElementData> into)
        {
            foreach (var e in elements)
            {
                into[e.Id] = e;
                if (e.Children.Count > 0) FlattenElements(e.Children, into);
            }
        }

        private void DrawReviewList()
        {
            EditorGUILayout.LabelField($"Review ({_matchResults.Count} elements)", EditorStyles.boldLabel);
            _reviewScroll = EditorGUILayout.BeginScrollView(_reviewScroll);
            foreach (var row in _matchResults)
            {
                DrawReviewRow(row);
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawReviewRow(MatchResultEntry row)
        {
            EditorGUILayout.BeginHorizontal("box");

            _elementById.TryGetValue(row.ElementId, out var element);
            DrawPreviewBox(GetElementPreview(element));

            EditorGUILayout.BeginVertical(GUILayout.Width(160));
            GUILayout.Label(row.ElementId, EditorStyles.boldLabel);
            GUILayout.Label(row.Status, StatusStyle(row.Status));
            EditorGUILayout.EndVertical();

            CatalogEntryData catEntry = null;
            Texture2D catTex = null;
            if (row.MatchedAssetId != null && _catalogById.TryGetValue(row.MatchedAssetId, out catEntry))
            {
                catTex = GetCatalogPreview(catEntry);
            }
            DrawPreviewBox(catTex);
            GUILayout.Label(row.MatchedAssetId ?? "(none)", GUILayout.Width(160));

            GUI.enabled = row.Status == "uncertain";
            if (GUILayout.Button("Accept", GUILayout.Width(70))) Accept(row);

            GUI.enabled = row.Status != "missing";
            if (GUILayout.Button("Reject", GUILayout.Width(70))) Reject(row);

            GUI.enabled = true;
            if (GUILayout.Button("Reassign", GUILayout.Width(70))) ShowReassignMenu(row);

            EditorGUILayout.EndHorizontal();
        }

        private static void DrawPreviewBox(Texture2D tex)
        {
            var content = tex != null ? new GUIContent(tex) : GUIContent.none;
            GUILayout.Box(content, GUILayout.Width(64), GUILayout.Height(64));
        }

        private static GUIStyle StatusStyle(string status)
        {
            var style = new GUIStyle(EditorStyles.miniLabel) { fontStyle = FontStyle.Bold };
            Color color;
            if (status == "matched") color = new Color(0.35f, 0.75f, 0.35f);
            else if (status == "uncertain") color = new Color(0.9f, 0.65f, 0.15f);
            else if (status == "missing") color = new Color(0.85f, 0.3f, 0.3f);
            else color = Color.gray;
            style.normal.textColor = color;
            return style;
        }

        private void Accept(MatchResultEntry row)
        {
            row.Status = "matched";
        }

        private void Reject(MatchResultEntry row)
        {
            row.Status = "missing";
            row.MatchedAssetId = null;
            // Described the old asset - stale/misleading now, and the
            // schema marks `resize` optional (not nullable), so dropping
            // it entirely is the correct way to clear it, not nulling it.
            row.RawResize = null;
        }

        private void ShowReassignMenu(MatchResultEntry row)
        {
            var menu = new GenericMenu();
            // Text-only catalog id list for this pass, not a thumbnail
            // grid - a nicer picker is a clear future enhancement, not
            // blocking this one.
            foreach (var entry in _catalog)
            {
                var id = entry.Id;
                menu.AddItem(new GUIContent(id), id == row.MatchedAssetId, () => Reassign(row, id));
            }
            menu.ShowAsContext();
        }

        private void Reassign(MatchResultEntry row, string catalogId)
        {
            row.MatchedAssetId = catalogId;
            row.Status = "matched";
            row.RawResize = null;
            // RawSignals deliberately left as-is: schema-required, can't
            // cleanly null it, and understood to be stale after a human
            // override - NodeBuilder never reads it either way.
            Repaint();
        }

        private Texture2D GetElementPreview(ElementData element)
        {
            if (element?.FigmaNodeId == null) return null;
            if (!_elementThumbnails.TryGetValue(element.FigmaNodeId, out var dataUri)) return null;
            return GetOrDecode("elem:" + element.FigmaNodeId, dataUri);
        }

        private Texture2D GetCatalogPreview(CatalogEntryData entry)
        {
            if (entry?.Thumbnail == null) return null;
            return GetOrDecode("cat:" + entry.Id, entry.Thumbnail);
        }

        private Texture2D GetOrDecode(string key, string dataUri)
        {
            if (_textureCache.TryGetValue(key, out var cached) && cached != null) return cached;
            var tex = DecodeDataUriToTexture(dataUri);
            _textureCache[key] = tex;
            return tex;
        }

        // Matches RenderedThumbnail.cs's own inverse encode path
        // ("data:image/png;base64," + Convert.ToBase64String(png)) already
        // in this codebase.
        private static Texture2D DecodeDataUriToTexture(string dataUri)
        {
            if (string.IsNullOrEmpty(dataUri)) return null;
            var comma = dataUri.IndexOf(',');
            var base64 = comma >= 0 ? dataUri.Substring(comma + 1) : dataUri;

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                return null;
            }

            // hideFlags: these are scratch Editor-only preview textures,
            // never meant to become project assets or leak into a scene -
            // plain `new Texture2D(...)` instances would otherwise show up
            // unexpectedly in the Hierarchy/Inspector.
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            if (!tex.LoadImage(bytes, markNonReadable: false))
            {
                UnityEngine.Object.DestroyImmediate(tex);
                return null;
            }
            return tex;
        }

        private void ClearTextureCache()
        {
            foreach (var tex in _textureCache.Values)
            {
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
            }
            _textureCache.Clear();
        }

        private void DrawAssembleControls()
        {
            if (GUILayout.Button("Save & Assemble"))
            {
                SaveAndAssemble();
            }
        }

        private void SaveAndAssemble()
        {
            try
            {
                AssemblerJson.WriteMatchResults(_matchResults, MatchResultPath);

                var canvas = CanvasScaffold.CreateRootCanvas(_elementTree);
                var root = NodeBuilder.BuildElement(canvas.transform, _elementTree, _matchResults, _catalog);
                var outputPath = RunAssemble.DefaultOutputPath(_elementTree.FrameId);
                var saved = PrefabWriter.SaveAsPrefab(root, outputPath);

                _statusMessage = $"Assembled -> {saved}";
            }
            catch (Exception ex)
            {
                _statusMessage = $"Assemble failed: {ex.Message}";
                Debug.LogException(ex);
            }
        }
    }
}
