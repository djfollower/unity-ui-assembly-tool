using System;
using System.IO;
using System.Linq;
using UiAssemblerSlice.Editor.Catalog;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace UiAssemblerSlice.Editor.Batch
{
    /// Ad-hoc verification entry points used while building the slice, run
    /// via -executeMethod against the real Melon project. Not a product
    /// deliverable itself (see RunCatalogBuild / RunAssemble for those).
    public static class SmokeTest
    {
        /// T1.2: proves a trivial -executeMethod round-trip works in batch
        /// mode before any real logic is written against it, per the plan's
        /// build-time risk mitigation for Unity automation quirks.
        ///
        /// Usage: Unity -batchmode -nographics -projectPath <path>
        ///   -executeMethod UiAssemblerSlice.Editor.Batch.SmokeTest.Run -quit
        public static void Run()
        {
            Debug.Log("UiAssemblerSlice.Editor.Batch.SmokeTest.Run: OK");
        }

        /// T1.4 verification: runs AssetDiscovery against the chosen feature
        /// folder and logs a summary.
        public static void RunAssetDiscovery()
        {
            const string featureFolder = "Assets/Textures/UI/UI Elements";
            var discovered = AssetDiscovery.DiscoverFeatureFolder(featureFolder);

            var sprites = discovered.Where(a => a.AssetType == "sprite").ToList();
            var prefabs = discovered.Where(a => a.AssetType == "prefab").ToList();

            Debug.Log($"AssetDiscovery: {sprites.Count} sprites, {prefabs.Count} prefabs in {featureFolder}");
            foreach (var sprite in sprites)
            {
                Debug.Log($"  sprite: {sprite.Path}");
            }
            foreach (var prefab in prefabs)
            {
                Debug.Log($"  prefab: {prefab.Path} -> refs [{string.Join(", ", prefab.ReferencedSpritePaths)}]");
            }
        }

        /// T1.5 risk probe: does GPU-based RenderTexture/Blit/ReadPixels
        /// work under -batchmode -nographics on this machine? Per the plan's
        /// build-time risk table, this must be answered before committing to
        /// a live-render thumbnail pipeline vs. a CPU-side pixel compositor.
        public static void ProbeNographicsRendering()
        {
            const string spritePath = "Assets/Textures/UI/UI Elements/button_frame_blue.png";
            var sourceTex = AssetDatabase.LoadAssetAtPath<Texture2D>(spritePath);
            if (sourceTex == null)
            {
                Debug.LogError($"ProbeNographicsRendering: could not load {spritePath}");
                return;
            }

            var rt = RenderTexture.GetTemporary(sourceTex.width, sourceTex.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(sourceTex, rt);

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var readable = new Texture2D(sourceTex.width, sourceTex.height, TextureFormat.RGBA32, false);
            readable.ReadPixels(new Rect(0, 0, sourceTex.width, sourceTex.height), 0, 0);
            readable.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            var pixels = readable.GetPixels32();
            int nonTransparentOpaque = pixels.Count(p => p.a > 10);
            int distinctColors = pixels.Select(p => (p.r, p.g, p.b, p.a)).Distinct().Count();

            var outPath = Path.Combine(Application.dataPath, "..", "..", "unity-ui-assembly-tool", "scripts", "logs", "nographics-probe.png");
            File.WriteAllBytes(Path.GetFullPath(outPath), readable.EncodeToPNG());

            Debug.Log($"ProbeNographicsRendering: OK - {pixels.Length} px, {nonTransparentOpaque} with alpha>10, {distinctColors} distinct colors, wrote {Path.GetFullPath(outPath)}");
        }

        /// T1.5 verification: probes + renders thumbnails for one 9-sliced
        /// sprite (button_frame_blue, spriteBorder set) and one plain sprite
        /// (icon_heart, no border), writing both PNGs to scripts/logs/ for
        /// visual inspection.
        public static void RunRenderProbe()
        {
            const string featureFolder = "Assets/Textures/UI/UI Elements";
            var slicedAsset = AssetDiscovery.DiscoverFeatureFolder(featureFolder)
                .First(a => a.Path.EndsWith("button_frame_blue.png"));
            var simpleAsset = AssetDiscovery.DiscoverFeatureFolder(featureFolder)
                .First(a => a.Path.EndsWith("icon_heart.png"));

            foreach (var asset in new[] { slicedAsset, simpleAsset })
            {
                var metadata = RenderMetadataProbe.Probe(asset);
                Debug.Log($"RenderMetadataProbe [{asset.Path}]: imageType={metadata.ImageType} " +
                          $"border=[{string.Join(",", metadata.Border)}] ppu={metadata.Ppu} " +
                          $"ppuMultiplier={metadata.PpuMultiplier} native={metadata.NativeWidth}x{metadata.NativeHeight} " +
                          $"tint={metadata.TintHex ?? "null"}");

                var base64 = RenderedThumbnail.RenderToBase64Png(asset, metadata, 256);
                var pngBytes = Convert.FromBase64String(base64.Substring("data:image/png;base64,".Length));
                var fileName = Path.GetFileNameWithoutExtension(asset.Path) + "-thumbnail.png";
                var outPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "unity-ui-assembly-tool", "scripts", "logs", fileName));
                File.WriteAllBytes(outPath, pngBytes);
                Debug.Log($"RenderedThumbnail [{asset.Path}]: wrote {outPath} ({pngBytes.Length} bytes)");
            }
        }

        /// Diagnostic: dumps every Image component under a prefab's
        /// hierarchy with its active/enabled/alpha/sprite state, to
        /// understand exactly why ResolveMainImage's visibility filter
        /// rejects a given prefab.
        public static void DumpImageStates()
        {
            const string prefabPath = "Assets/Prefabs/UI/ButtonFrame.prefab";
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (go == null)
            {
                Debug.LogError($"DumpImageStates: could not load {prefabPath}");
                return;
            }

            Debug.Log($"DumpImageStates: {prefabPath}, root activeSelf={go.activeSelf} activeInHierarchy={go.activeInHierarchy}");

            foreach (var img in go.GetComponentsInChildren<Image>(true))
            {
                var path = GetHierarchyPath(img.transform, go.transform);
                Debug.Log($"  [{path}] enabled={img.enabled} activeSelf={img.gameObject.activeSelf} " +
                          $"activeInHierarchy={img.gameObject.activeInHierarchy} sprite={(img.sprite != null ? img.sprite.name : "null")} " +
                          $"color={img.color} rect={img.rectTransform.rect} raycastTarget={img.raycastTarget}");
            }

            var selectable = go.GetComponentInChildren<Selectable>(true);
            if (selectable != null)
            {
                Debug.Log($"  Selectable found on [{GetHierarchyPath(selectable.transform, go.transform)}], " +
                          $"targetGraphic={(selectable.targetGraphic != null ? GetHierarchyPath(selectable.targetGraphic.transform, go.transform) : "null")}");
            }
            else
            {
                Debug.Log("  No Selectable found under this prefab.");
            }
        }

        /// Diagnostic: investigating a rendering bug where some sprite
        /// thumbnails show a duplicated/scalloped artifact instead of their
        /// real art (button_x, button_settings - both need 2x upscale to
        /// the 256px canonical thumbnail size; button_red/button_frame_x,
        /// unaffected, are 256x256 or need no upscale). Checks two
        /// hypotheses: (a) the source PNG is a multi-sprite sheet, so
        /// AssetDatabase.LoadAssetAtPath<Texture2D> returns the whole sheet
        /// rather than one sprite's region (AssetDiscovery.cs's own comment
        /// on "multi-sprite atlases can repeat a guid" suggests this is a
        /// real possibility in this project); (b) the texture's Wrap Mode
        /// import setting is Repeat instead of Clamp, causing GPU sampling
        /// to bleed/tile when magnified.
        public static void DiagnoseThumbnailArtifact()
        {
            var paths = new[]
            {
                "Assets/Textures/UI/UI Elements/button_x.png",
                "Assets/Textures/UI/UI Elements/button_settings.png",
                "Assets/Textures/UI/UI Elements/button_red.png",
                "Assets/Textures/UI/UI Elements/button_frame_x.png",
            };

            foreach (var path in paths)
            {
                var allAssets = AssetDatabase.LoadAllAssetsAtPath(path);
                var sprites = allAssets.OfType<Sprite>().ToList();
                var looseTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;

                Debug.Log($"DiagnoseThumbnailArtifact [{path}]:");
                Debug.Log($"  Loose Texture2D (AssetDatabase.LoadAssetAtPath): {(looseTexture != null ? $"{looseTexture.width}x{looseTexture.height}, instanceID={looseTexture.GetInstanceID()}, wrapMode={looseTexture.wrapMode}, filterMode={looseTexture.filterMode}" : "null")}");
                Debug.Log($"  Sprite sub-assets: {sprites.Count}");
                foreach (var sprite in sprites)
                {
                    var spriteTex = sprite.texture;
                    Debug.Log($"    - name={sprite.name} rect={sprite.rect} border={sprite.border} textureRectOffset={sprite.textureRectOffset} textureRect={sprite.textureRect}");
                    Debug.Log($"      sprite.texture: {(spriteTex != null ? $"{spriteTex.width}x{spriteTex.height}, instanceID={spriteTex.GetInstanceID()}, name={spriteTex.name}" : "null")}");
                    Debug.Log($"      sprite.texture == looseTexture: {spriteTex == looseTexture}");
                    Debug.Log($"      packed={sprite.packed} packingMode={sprite.packingMode} associatedAlpha={sprite.associatedAlphaSplitTexture}");
                }
                if (importer != null)
                {
                    Debug.Log($"  TextureImporter: spriteImportMode={importer.spriteImportMode} wrapMode={importer.wrapMode} isReadable={importer.isReadable}");
                }
            }
        }

        /// Isolated, minimal test to settle exactly what Graphics.DrawTexture's
        /// border ints mean (source-texture pixels vs. destination pixels) -
        /// three real-pipeline fix attempts at the button_x/button_settings
        /// thumbnail bug all failed in different ways, suggesting a wrong
        /// mental model of this API rather than a units/trim bug specifically.
        /// Builds a small synthetic texture with a distinctly-colored 9-slice
        /// pattern (each region a different flat color) and draws it twice,
        /// once with border=10 (literal source-texture value, unscaled) and
        /// once with border=50 (source value x5, matching this project's
        /// existing scale-multiplication approach), both into a 200x200
        /// (5x upscale) destination - whichever produces VISUALLY CORRECT,
        /// undistorted corners at the expected proportion answers the
        /// question directly instead of by inference from docs/memory.
        public static void ProbeDrawTextureBorderSemantics()
        {
            const int texSize = 40;
            const int border = 10;
            const int destSize = 200; // 5x upscale from texSize

            var tex = new Texture2D(texSize, texSize, TextureFormat.RGBA32, false);
            var pixels = new Color32[texSize * texSize];
            for (var y = 0; y < texSize; y++)
            {
                for (var x = 0; x < texSize; x++)
                {
                    Color32 c;
                    var left = x < border;
                    var right = x >= texSize - border;
                    var bottom = y < border; // texture row 0 = bottom in Unity's UV convention
                    var top = y >= texSize - border;
                    if (left && bottom) c = new Color32(255, 0, 0, 255); // red: bottom-left corner
                    else if (right && bottom) c = new Color32(0, 255, 0, 255); // green: bottom-right corner
                    else if (left && top) c = new Color32(0, 0, 255, 255); // blue: top-left corner
                    else if (right && top) c = new Color32(255, 255, 0, 255); // yellow: top-right corner
                    else if (left || right || top || bottom) c = new Color32(255, 0, 255, 255); // magenta: edges
                    else c = new Color32(255, 255, 255, 255); // white: middle
                    pixels[y * texSize + x] = c;
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply();

            void RenderVariant(int borderArg, string label)
            {
                var rt = RenderTexture.GetTemporary(destSize, destSize, 0, RenderTextureFormat.ARGB32);
                var prevActive = RenderTexture.active;
                try
                {
                    RenderTexture.active = rt;
                    GL.Clear(true, true, new Color(0.2f, 0.2f, 0.2f, 1f));
                    GL.PushMatrix();
                    GL.LoadPixelMatrix(0, destSize, destSize, 0);
                    Graphics.DrawTexture(new Rect(0, 0, destSize, destSize), tex, new Rect(0, 0, 1, 1),
                        borderArg, borderArg, borderArg, borderArg, Color.white);
                    GL.PopMatrix();

                    var readable = new Texture2D(destSize, destSize, TextureFormat.RGBA32, false);
                    readable.ReadPixels(new Rect(0, 0, destSize, destSize), 0, 0);
                    readable.Apply();
                    var outPath = Path.GetFullPath(Path.Combine(
                        Application.dataPath, "..", "..", "unity-ui-assembly-tool", "scripts", "logs", $"drawtexture-border-probe-{label}.png"));
                    File.WriteAllBytes(outPath, readable.EncodeToPNG());
                    UnityEngine.Object.DestroyImmediate(readable);
                    Debug.Log($"ProbeDrawTextureBorderSemantics [{label}]: borderArg={borderArg}, wrote {outPath}");
                }
                finally
                {
                    RenderTexture.active = prevActive;
                    RenderTexture.ReleaseTemporary(rt);
                }
            }

            RenderVariant(border, "unscaled-10");
            RenderVariant(border * 5, "scaled-50");
        }

        private static string GetHierarchyPath(Transform t, Transform root)
        {
            if (t == root) return t.name;
            var path = t.name;
            var current = t.parent;
            while (current != null && current != root)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }
            return path;
        }
    }
}
