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
