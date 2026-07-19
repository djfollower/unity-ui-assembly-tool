using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UiAssemblerSlice.Editor.Assembler;
using UiAssemblerSlice.Editor.Batch;
using UiAssemblerSlice.Editor.Catalog;
using UiAssemblerSlice.Editor.Matcher;
using UnityEditor;
using UnityEngine;

namespace UiAssemblerSlice.Editor.Review
{
    /// Stage 3: runs the matcher in-process against the Figma plugin's
    /// exported bundle (element_tree + element_thumbnails +
    /// element_fallback_captures - see figma-plugin/ui.html's
    /// buildExportBundle), shows one review row per match result
    /// (accept/reject/reassign), and assembles the final prefab in-process -
    /// the same three calls RunAssemble.cs's batchmode entry point makes,
    /// just triggered live instead of headless.
    ///
    /// No subprocess/Node involvement anymore (previously spawned
    /// `npm run cli -- reduce-from-selection` + `-- match` via
    /// PipelineRunner) - the plugin already emits a reduced element-tree
    /// directly, and MatchElementTree.Run replaces the `match` CLI step.
    ///
    /// Known limitation, not fixed here: none of this window's state
    /// survives a domain reload (e.g. editing a C# script while the window
    /// is open) - all fields are plain `private`, deliberately not
    /// [SerializeField], since a Dictionary/Texture2D aren't things Unity's
    /// serializer should be attempting to persist. Re-run "Load
    /// match-result.json for review" after a reload if needed.
    public class ReviewWindow : EditorWindow
    {
        [MenuItem("UI Assembler/Review Window")]
        public static void Open()
        {
            GetWindow<ReviewWindow>("UI Assembler Review");
        }

        // ---- user-entered inputs ----
        // The single bundled JSON figma-plugin/ui.html's Download button
        // produces (element_tree/element_thumbnails/element_fallback_captures
        // top-level keys) - not a raw, pre-reduction plugin export anymore.
        private string _exportBundlePath = "";
        private string _catalogPath = ".cache/catalog.json";
        // Where to read/write element-tree.json / match-result.json /
        // element-thumbnails.json / element-fallback-captures.json. Blank
        // (the default) falls back to RepoRoot's own guess below, for
        // backward compatibility with the "monorepo cloned as a sibling
        // folder next to the Unity project" convention - but that guess
        // breaks whenever this package is installed via a git-URL UPM
        // dependency instead of a local sibling checkout (it resolves into
        // Library/PackageCache's hashed folder, not a sibling
        // "unity-ui-assembly-tool" directory), and is fragile in general
        // across drive letters/OSes. Set this explicitly instead of relying
        // on the guess if RunMatcher/LoadReviewData can't find their files.
        private string _cacheFolderPath = "";

        // ---- pipeline ----
        private string _statusMessage = "";

        // ---- loaded review data ----
        private ElementTreeData _elementTree;
        private List<MatchResultEntry> _matchResults;
        private List<CatalogEntryData> _catalog;
        private Dictionary<string, string> _elementThumbnails = new Dictionary<string, string>();
        private Dictionary<string, string> _elementFallbackCaptures = new Dictionary<string, string>();
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

        // Resolves to _cacheFolderPath when set (absolute, or relative to
        // RepoRoot), else falls back to RepoRoot's own guessed .cache/ -
        // see _cacheFolderPath's own comment on why that guess isn't always
        // reliable.
        private string CacheFolder =>
            string.IsNullOrWhiteSpace(_cacheFolderPath)
                ? Path.Combine(RepoRoot, ".cache")
                : ResolvePath(_cacheFolderPath, RepoRoot);

        private string ElementTreePath => Path.Combine(CacheFolder, "element-tree.json");
        private string MatchResultPath => Path.Combine(CacheFolder, "match-result.json");
        private string ElementThumbnailsPath => Path.Combine(CacheFolder, "element-thumbnails.json");
        private string ElementFallbackCapturesPath => Path.Combine(CacheFolder, "element-fallback-captures.json");

        private static string ResolvePath(string userPath, string repoRoot)
        {
            if (string.IsNullOrWhiteSpace(userPath)) return null;
            return Path.IsPathRooted(userPath) ? userPath : Path.GetFullPath(Path.Combine(repoRoot, userPath));
        }

        private void OnDestroy()
        {
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
            _exportBundlePath = EditorGUILayout.TextField("Export bundle JSON", _exportBundlePath);
            if (GUILayout.Button("Browse...", GUILayout.Width(70)))
            {
                var picked = EditorUtility.OpenFilePanel("Select Figma plugin export bundle", "", "json");
                if (!string.IsNullOrEmpty(picked)) _exportBundlePath = picked;
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

            EditorGUILayout.BeginHorizontal();
            _cacheFolderPath = EditorGUILayout.TextField(
                new GUIContent("Cache folder", "Leave blank to use the sibling-repo-checkout guess. Set explicitly if this package was installed via a git URL, or if RunMatcher/Load can't find their files."),
                _cacheFolderPath);
            if (GUILayout.Button("Browse...", GUILayout.Width(70)))
            {
                var picked = EditorUtility.OpenFolderPanel("Select cache folder", "", "");
                if (!string.IsNullOrEmpty(picked)) _cacheFolderPath = picked;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField("-> " + ElementTreePath, EditorStyles.miniLabel);
            EditorGUILayout.LabelField("-> " + MatchResultPath, EditorStyles.miniLabel);
            EditorGUILayout.LabelField("-> " + ElementThumbnailsPath, EditorStyles.miniLabel);
        }

        private void DrawPipelineControls()
        {
            // Deliberately NOT gated on _exportBundlePath being non-empty -
            // that used to silently disable the button with no feedback at
            // all (confusing - "the button is locked"). Always clickable;
            // RunMatcher() itself reports a clear _statusMessage if the path
            // is missing, same as any other input-validation error here.
            if (GUILayout.Button("Run matcher"))
            {
                RunMatcher();
            }

            if (GUILayout.Button("Load match-result.json for review"))
            {
                LoadReviewData();
            }

            if (string.IsNullOrWhiteSpace(_exportBundlePath))
            {
                EditorGUILayout.HelpBox("Set an export bundle JSON path (or Browse...) to run the matcher.", MessageType.None);
            }
        }

        // Runs the matcher in-process: unpacks the plugin's export bundle
        // into the conventional .cache/ files (same fixed locations/shapes
        // the old Node CLI wrote, so LoadReviewData/AssemblerJson's loaders
        // don't need to change), then calls MatchElementTree.Run directly -
        // no subprocess, no Node/npm involved.
        private void RunMatcher()
        {
            var repoRoot = RepoRoot;
            var exportBundleAbs = ResolvePath(_exportBundlePath, repoRoot);
            var catalogAbs = ResolvePath(_catalogPath, repoRoot) ?? Path.Combine(repoRoot, ".cache", "catalog.json");

            if (exportBundleAbs == null)
            {
                _statusMessage = "Set an export bundle JSON path first.";
                return;
            }

            try
            {
                var bundle = (Dictionary<string, object>)JsonParser.Parse(File.ReadAllText(exportBundleAbs));
                Directory.CreateDirectory(Path.GetDirectoryName(ElementTreePath) ?? ".");
                File.WriteAllText(ElementTreePath, JsonWriter.Write(bundle["element_tree"]));
                File.WriteAllText(ElementThumbnailsPath, JsonWriter.Write(bundle["element_thumbnails"]));
                File.WriteAllText(ElementFallbackCapturesPath, JsonWriter.Write(bundle["element_fallback_captures"]));

                var elementTree = AssemblerJson.LoadElementTree(ElementTreePath);
                var catalog = AssemblerJson.LoadCatalog(catalogAbs);
                var elementThumbnails = AssemblerJson.LoadElementThumbnails(ElementThumbnailsPath);
                var elementFallbackCaptures = AssemblerJson.LoadElementFallbackCaptures(ElementFallbackCapturesPath);

                var matchResults = MatchElementTree.Run(elementTree, catalog, elementThumbnails, elementFallbackCaptures);
                AssemblerJson.WriteMatchResults(matchResults, MatchResultPath);

                _statusMessage = "Matcher finished";
                LoadReviewData();
            }
            catch (Exception ex)
            {
                _statusMessage = $"Matcher failed: {ex.Message}";
                Debug.LogException(ex);
            }
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
                _elementFallbackCaptures = File.Exists(ElementFallbackCapturesPath)
                    ? AssemblerJson.LoadElementFallbackCaptures(ElementFallbackCapturesPath)
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
            // fallback_eligible: the small selection thumbnail is less
            // useful here than the real hi-res capture that's the whole
            // reason this row exists - show that instead when available.
            var leftPreview = row.Status == "fallback_eligible"
                ? GetFallbackCapturePreview(element) ?? GetElementPreview(element)
                : GetElementPreview(element);
            DrawPreviewBox(leftPreview);

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

            GUI.enabled = row.Status == "fallback_eligible";
            if (GUILayout.Button("Import as New Asset", GUILayout.Width(140))) ImportFallbackAsset(row);

            GUI.enabled = true;
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
            else if (status == "fallback_eligible") color = new Color(0.55f, 0.45f, 0.9f);
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

        // Imports a fallback_eligible row's hi-res Combine-group capture as
        // a real, reusable catalog entry - same reasoning as
        // NodeBuilder.SaveCompositedSprite for why this must be a real file
        // on disk (AssetImportHelpers.ImportPngAsSprite), not an in-memory
        // Sprite. Reuses the exact same discovery/probe/thumbnail pieces
        // RunCatalogBuild.cs's own per-asset loop uses (DiscoveredAsset,
        // RenderMetadataProbe.Probe, RenderedThumbnail.RenderToPngBytes) -
        // confirmed via exploration these already handle a plain,
        // freshly-imported PNG with no 9-slice/tint data gracefully
        // (reports Simple/no-border/no-tint, no special-casing needed).
        private void ImportFallbackAsset(MatchResultEntry row)
        {
            try
            {
                if (!_elementById.TryGetValue(row.ElementId, out var element) || element.FigmaNodeId == null)
                {
                    throw new InvalidOperationException($"no element/figma_node_id found for '{row.ElementId}'");
                }
                if (!_elementFallbackCaptures.TryGetValue(element.FigmaNodeId, out var dataUri))
                {
                    throw new InvalidOperationException($"no fallback capture found for '{row.ElementId}'");
                }

                var comma = dataUri.IndexOf(',');
                var bytes = Convert.FromBase64String(comma >= 0 ? dataUri.Substring(comma + 1) : dataUri);

                var safeId = string.Join("_", element.Id.Split(Path.GetInvalidFileNameChars()));
                const string assetFolder = "Assets/_Generated/UIAssembler/FallbackAssets";
                var assetPath = $"{assetFolder}/{safeId}.png";
                AssetImportHelpers.ImportPngAsSprite(assetPath, bytes);

                var asset = new DiscoveredAsset(assetPath, AssetDatabase.AssetPathToGUID(assetPath), "sprite", Array.Empty<string>());
                var metadata = RenderMetadataProbe.Probe(asset);

                // Same catalogAbs LoadReviewData resolved _catalog from -
                // recomputed identically (deterministic given _catalogPath
                // is unchanged since load) so the appended entry lands in
                // the exact same file the rest of this window is using.
                var repoRoot = RepoRoot;
                var catalogAbs = ResolvePath(_catalogPath, repoRoot) ?? Path.Combine(repoRoot, ".cache", "catalog.json");
                var catalogDir = Path.GetDirectoryName(catalogAbs);

                // "Fallback__" prefix avoids colliding with feature-folder-
                // derived ids (RunCatalogBuild.cs's "<feature>__<name>"
                // convention) - this entry's "feature" is this import
                // action, not a scanned folder.
                var newId = $"Fallback__{safeId}";
                const int canonicalThumbnailSize = 256; // matches RunCatalogBuild.cs's own constant
                var thumbnailBytes = RenderedThumbnail.RenderToPngBytes(asset, metadata, canonicalThumbnailSize);
                var thumbnailRelativePath = $"thumbnails/{newId}.png";
                var thumbnailAbsolutePath = Path.Combine(catalogDir, "thumbnails", $"{newId}.png");
                Directory.CreateDirectory(Path.GetDirectoryName(thumbnailAbsolutePath));
                File.WriteAllBytes(thumbnailAbsolutePath, thumbnailBytes);

                var entry = CatalogAppender.BuildSpriteEntry(newId, assetPath, "Fallback", metadata, thumbnailRelativePath);
                CatalogAppender.AppendEntry(catalogAbs, entry);

                // Update in-memory state too, so it's usable without a
                // reload (same convention as Reassign, just for a brand-new
                // id the picker didn't have until now).
                var newCatalogEntry = new CatalogEntryData
                {
                    Id = newId,
                    Path = assetPath,
                    Type = "sprite",
                    ImageType = metadata.ImageType,
                    PpuMultiplier = metadata.PpuMultiplier,
                    Border = metadata.Border,
                    TintHex = metadata.TintHex,
                    ThumbnailPath = thumbnailAbsolutePath,
                };
                _catalog.Add(newCatalogEntry);
                _catalogById[newId] = newCatalogEntry;

                row.MatchedAssetId = newId;
                row.Status = "matched";
                row.RawResize = null;

                _statusMessage = $"Imported '{newId}' - added to catalog and matched to '{row.ElementId}'";
                Repaint();
            }
            catch (Exception ex)
            {
                _statusMessage = $"Import fallback asset failed: {ex.Message}";
                Debug.LogException(ex);
            }
        }

        private Texture2D GetElementPreview(ElementData element)
        {
            if (element?.FigmaNodeId == null) return null;
            if (!_elementThumbnails.TryGetValue(element.FigmaNodeId, out var dataUri)) return null;
            return GetOrDecode("elem:" + element.FigmaNodeId, dataUri);
        }

        private Texture2D GetFallbackCapturePreview(ElementData element)
        {
            if (element?.FigmaNodeId == null) return null;
            if (!_elementFallbackCaptures.TryGetValue(element.FigmaNodeId, out var dataUri)) return null;
            return GetOrDecode("fallback:" + element.FigmaNodeId, dataUri);
        }

        private Texture2D GetCatalogPreview(CatalogEntryData entry)
        {
            if (entry?.ThumbnailPath == null) return null;
            return GetOrLoad("cat:" + entry.Id, entry.ThumbnailPath);
        }

        private Texture2D GetOrDecode(string key, string dataUri)
        {
            if (_textureCache.TryGetValue(key, out var cached) && cached != null) return cached;
            var tex = DecodeDataUriToTexture(dataUri);
            _textureCache[key] = tex;
            return tex;
        }

        // Catalog thumbnails are files on disk now, not inline base64 (see
        // catalog-entry.schema.json's thumbnail_path / HANDOFF.md's
        // "Incremental catalog rebuild" - a real project's catalog can run
        // to thousands of entries, too much to keep inline in JSON).
        // AssemblerJson.LoadCatalog already resolved entry.ThumbnailPath to
        // an absolute path, so this just reads it.
        private Texture2D GetOrLoad(string key, string absolutePath)
        {
            if (_textureCache.TryGetValue(key, out var cached) && cached != null) return cached;
            var tex = LoadTextureFromFile(absolutePath);
            _textureCache[key] = tex;
            return tex;
        }

        private static Texture2D LoadTextureFromFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (IOException)
            {
                return null;
            }

            // hideFlags: same reasoning as DecodeDataUriToTexture below -
            // scratch Editor-only preview textures, never project assets.
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            if (!tex.LoadImage(bytes, markNonReadable: false))
            {
                UnityEngine.Object.DestroyImmediate(tex);
                return null;
            }
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
