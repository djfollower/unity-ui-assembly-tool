using System;
using System.Collections.Generic;
using System.IO;
using UiAssemblerSlice.Editor.Assembler;
using UnityEngine;

namespace UiAssemblerSlice.Editor.Batch
{
    /// Batch-mode pixel-level render of the assembled hierarchy, for an
    /// automated comparison against fixtures/frame-export.png - the gap
    /// HANDOFF.md flagged as "not attempted" after Week 3's assembler
    /// shipped (verification up to that point was the user's own live
    /// Editor review, not a repeatable artifact).
    ///
    /// Builds the exact same in-memory hierarchy RunAssemble.cs builds
    /// (CanvasScaffold + NodeBuilder - reused directly, not re-implemented,
    /// so this renders exactly what the real assembler produces) but never
    /// saves it as a prefab asset; it's a throwaway scene object, destroyed
    /// after the screenshot is captured.
    ///
    /// The render recipe (ScreenSpaceCamera Canvas + an orthographic camera
    /// targeting a RenderTexture + Canvas.ForceUpdateCanvases() before a
    /// synchronous Camera.Render()) is not new - it's the same one
    /// SmokeTest.CaptureCatalogEntryRender already validated for single
    /// catalog entries. What's new here is applying it to the FULL
    /// CanvasScaffold-built hierarchy instead of one sprite: CanvasScaffold
    /// always creates a ScreenSpaceOverlay Canvas (correct for the real
    /// saved prefab, which needs to behave like production UI), so this
    /// mutates renderMode/worldCamera on the returned Canvas afterward,
    /// screenshot-only - CanvasScaffold itself is untouched.
    ///
    /// Rendering at exactly canvas_reference resolution (not an arbitrary
    /// device size) is deliberate: NodeBuilder positions every element via
    /// absolute anchoredPosition/sizeDelta already in canvas_reference-space
    /// units (see NodeBuilder.PositionRect's own comment), which only maps
    /// 1:1 to on-screen pixels when CanvasScaler's computed scaleFactor is
    /// exactly 1 - true when the render target's size equals
    /// referenceResolution exactly (computeScaleFactor's log2 blend becomes
    /// log2(1)=0 on both axes). Rendering at any other resolution would
    /// require accounting for that scale factor a second time.
    ///
    /// Usage: Unity -batchmode -projectPath <path> (no -nographics, see
    ///   README.md/CLAUDE.md - it silently produces blank output otherwise)
    ///   -executeMethod UiAssemblerSlice.Editor.Batch.RunScreenshot.Run
    ///   -elementTreePath <path> -matchResultPath <path> [-catalogPath <path>]
    ///   -outputPath <png path> [-bgColor <#RRGGBBAA>] -quit
    public static class RunScreenshot
    {
        public static void Run()
        {
            var args = BatchArgs.ParseArgs(Environment.GetCommandLineArgs());
            var elementTreePath = RequireArg(args, "-elementTreePath");
            var matchResultPath = RequireArg(args, "-matchResultPath");
            var catalogPath = args.GetValueOrDefault("-catalogPath", DefaultCatalogPath());
            var outputPath = RequireArg(args, "-outputPath");
            // Transparent by default: frame-export.png's own background is
            // real mockup content this pipeline never claims to reproduce
            // (no scrim/background-color element exists as a matchable
            // asset in this fixture's catalog), so a transparent render
            // composites cleanly for a side-by-side or overlay diff without
            // guessing at a color to match.
            var bgColor = ParseColor(args.GetValueOrDefault("-bgColor", null)) ?? new Color(0, 0, 0, 0);

            var elementTree = AssemblerJson.LoadElementTree(elementTreePath);
            var matchResults = AssemblerJson.LoadMatchResults(matchResultPath);
            var catalog = AssemblerJson.LoadCatalog(catalogPath);

            var w = Mathf.RoundToInt(elementTree.CanvasReference.W);
            var h = Mathf.RoundToInt(elementTree.CanvasReference.H);

            Debug.Log($"RunScreenshot: rendering frame {elementTree.FrameId} at {w}x{h} -> {outputPath}");

            var canvas = CanvasScaffold.CreateRootCanvas(elementTree);
            NodeBuilder.BuildElement(canvas.transform, elementTree, matchResults, catalog);

            var camGo = new GameObject("ScreenshotCam", typeof(Camera));
            var cam = camGo.GetComponent<Camera>();
            cam.orthographic = true;
            // Orthographic projection has no perspective divide, so the
            // world-space height visible at any planeDistance is always
            // exactly 2 * orthographicSize regardless of distance - setting
            // it to h/2 makes 1 world unit = 1 render-target pixel, the
            // same relationship CaptureCatalogEntryRender already relies on.
            cam.orthographicSize = h / 2f;
            cam.aspect = (float)w / h;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 100f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = bgColor;

            var renderTexture = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32);
            var prevActive = RenderTexture.active;
            try
            {
                cam.targetTexture = renderTexture;

                // Screenshot-only mutation of the Canvas CanvasScaffold
                // built for real production use (Overlay) - never written
                // back to any saved asset, this whole hierarchy is
                // destroyed in the `finally` below.
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = cam;
                canvas.planeDistance = 10;

                // Nothing ticks Play Mode or an Editor frame between
                // building this UI and the synchronous Render() call below,
                // so the Canvas's own per-frame geometry rebuild (normally
                // automatic) has to be forced explicitly - same requirement
                // CaptureCatalogEntryRender already documented.
                Canvas.ForceUpdateCanvases();

                cam.Render();

                RenderTexture.active = renderTexture;
                var readable = new Texture2D(w, h, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                readable.Apply();
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
                File.WriteAllBytes(outputPath, readable.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(readable);
                Debug.Log($"RunScreenshot: wrote {outputPath}");
            }
            finally
            {
                RenderTexture.active = prevActive;
                cam.targetTexture = null;
                RenderTexture.ReleaseTemporary(renderTexture);
                UnityEngine.Object.DestroyImmediate(camGo);
                UnityEngine.Object.DestroyImmediate(canvas.gameObject);
            }
        }

        private static string RequireArg(Dictionary<string, string> args, string name)
        {
            if (!args.TryGetValue(name, out var value))
            {
                throw new ArgumentException($"RunScreenshot: missing required argument {name}");
            }
            return value;
        }

        private static Color? ParseColor(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return null;
            if (!ColorUtility.TryParseHtmlString(hex, out var color))
            {
                throw new ArgumentException($"RunScreenshot: -bgColor \"{hex}\" is not a valid #RRGGBB[AA] color");
            }
            return color;
        }

        // Same convention as RunAssemble.cs's DefaultCatalogPath - an
        // OS-absolute path, since catalog.json is read via File.ReadAllText,
        // not AssetDatabase.
        private static string DefaultCatalogPath()
        {
            return Path.GetFullPath(Path.Combine(
                Application.dataPath, "..", "..", "unity-ui-assembly-tool", ".cache", "catalog.json"));
        }
    }
}
