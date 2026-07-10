using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UiAssemblerSlice.Editor.Catalog;
using UnityEditor;
using UnityEngine;

namespace UiAssemblerSlice.Editor.Batch
{
    /// T1.6: -executeMethod entry point. Runs AssetDiscovery + RenderMetadataProbe
    /// + RenderedThumbnail end-to-end and writes catalog.json conforming to
    /// catalog-entry.schema.json. Invoked via scripts/build-catalog.sh.
    ///
    /// Usage: Unity -batchmode -projectPath <path> (no -nographics - see README)
    ///   -executeMethod UiAssemblerSlice.Editor.Batch.RunCatalogBuild.Run
    ///   -featureFolder <path> -outputPath <path>
    ///   [-extraPrefabPaths "Assets/Prefabs/UI/A.prefab,Assets/Prefabs/UI/B.prefab"] -quit
    public static class RunCatalogBuild
    {
        private const int CanonicalThumbnailSize = 256;

        public static void Run()
        {
            var args = ParseArgs(Environment.GetCommandLineArgs());
            var featureFolder = args.GetValueOrDefault("-featureFolder", "Assets/Textures/UI/UI Elements");
            var outputPath = args.GetValueOrDefault("-outputPath", DefaultOutputPath());
            var feature = SanitizeFeatureName(featureFolder);

            Debug.Log($"RunCatalogBuild: scanning {featureFolder} (feature={feature})");

            var discovered = AssetDiscovery.DiscoverFeatureFolder(featureFolder);
            var sprites = discovered.Where(a => a.AssetType == "sprite").ToList();
            var skippedPrefabs = discovered.Where(a => a.AssetType == "prefab").ToList();
            if (skippedPrefabs.Count > 0)
            {
                Debug.LogWarning($"RunCatalogBuild: skipping {skippedPrefabs.Count} prefab(s) from {featureFolder} - " +
                                  "not explicitly requested via -extraPrefabPaths.");
            }

            // Explicit opt-in list rather than a whole-folder scan: this
            // project's other prefab folders (Assets/Prefabs/UI/ etc.) hold
            // dozens of unrelated, complex prefabs outside this feature's
            // scope - only pull in specific ones added as fixture test cases
            // (e.g. a hand-made tinted variant for Gate 2's tinted-asset
            // requirement).
            var extraPrefabPaths = (args.GetValueOrDefault("-extraPrefabPaths", "") ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim());
            var extraPrefabs = extraPrefabPaths.Select(p => new DiscoveredAsset(
                p, AssetDatabase.AssetPathToGUID(p), "prefab", Array.Empty<string>()));

            var entries = new List<CatalogEntryData>();
            foreach (var asset in sprites.Concat(extraPrefabs))
            {
                var metadata = RenderMetadataProbe.Probe(asset);
                var thumbnail = RenderedThumbnail.RenderToBase64Png(asset, metadata, CanonicalThumbnailSize);
                var name = Path.GetFileNameWithoutExtension(asset.Path);

                entries.Add(new CatalogEntryData
                {
                    Id = $"{feature}__{name}",
                    Path = asset.Path,
                    Type = asset.AssetType,
                    Feature = feature,
                    ImageType = metadata.ImageType,
                    Border = metadata.Border,
                    Ppu = metadata.Ppu,
                    PpuMultiplier = metadata.PpuMultiplier,
                    NativeW = metadata.NativeWidth,
                    NativeH = metadata.NativeHeight,
                    TintHex = metadata.TintHex,
                    Thumbnail = thumbnail,
                    // Heuristic pending real usage data (see RenderMetadataProbe's
                    // notes on ppu_multiplier/tint - this folder has no prefabs to
                    // read a real role/interactivity signal from either).
                    Role = "base",
                    Interactive = name.ToLowerInvariant().Contains("button"),
                    // T1.7 fills this in via an LLM call on the rendered thumbnail.
                    VisualDescription = "",
                });
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
            File.WriteAllText(outputPath, ToJson(entries));
            Debug.Log($"RunCatalogBuild: wrote {entries.Count} entries to {outputPath}");
        }

        private static string DefaultOutputPath()
        {
            return Path.GetFullPath(Path.Combine(
                Application.dataPath, "..", "..", "unity-ui-assembly-tool", ".cache", "catalog.json"));
        }

        private static string SanitizeFeatureName(string featureFolder)
        {
            var last = featureFolder.TrimEnd('/').Split('/').Last();
            return last.Replace(" ", "");
        }

        private static Dictionary<string, string> ParseArgs(string[] args)
        {
            var result = new Dictionary<string, string>();
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i].StartsWith("-") && !args[i + 1].StartsWith("-"))
                {
                    result[args[i]] = args[i + 1];
                }
            }
            return result;
        }

        private struct CatalogEntryData
        {
            public string Id;
            public string Path;
            public string Type;
            public string Feature;
            public string ImageType;
            public float[] Border;
            public float Ppu;
            public float PpuMultiplier;
            public float NativeW;
            public float NativeH;
            public string TintHex;
            public string Thumbnail;
            public string Role;
            public bool Interactive;
            public string VisualDescription;
        }

        // Hand-rolled rather than JsonUtility: JsonUtility can't serialize a
        // top-level array, and can't emit JSON null for a string field
        // (tint needs to be null, not "", when untinted).
        private static string ToJson(List<CatalogEntryData> entries)
        {
            var sb = new StringBuilder();
            sb.Append("[\n");
            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                sb.Append("  {\n");
                sb.Append($"    \"id\": {JsonString(e.Id)},\n");
                sb.Append($"    \"path\": {JsonString(e.Path)},\n");
                sb.Append($"    \"type\": {JsonString(e.Type)},\n");
                sb.Append($"    \"feature\": {JsonString(e.Feature)},\n");
                sb.Append("    \"render\": {\n");
                sb.Append($"      \"image_type\": {JsonString(e.ImageType)},\n");
                sb.Append($"      \"border\": [{string.Join(", ", e.Border.Select(JsonNumber))}],\n");
                sb.Append($"      \"ppu\": {JsonNumber(e.Ppu)},\n");
                sb.Append($"      \"ppu_multiplier\": {JsonNumber(e.PpuMultiplier)},\n");
                sb.Append($"      \"native_size\": {{ \"w\": {JsonNumber(e.NativeW)}, \"h\": {JsonNumber(e.NativeH)} }},\n");
                sb.Append($"      \"tint\": {(e.TintHex == null ? "null" : JsonString(e.TintHex))}\n");
                sb.Append("    },\n");
                sb.Append($"    \"thumbnail\": {JsonString(e.Thumbnail)},\n");
                sb.Append($"    \"role\": {JsonString(e.Role)},\n");
                sb.Append($"    \"interactive\": {(e.Interactive ? "true" : "false")},\n");
                sb.Append($"    \"visual_description\": {JsonString(e.VisualDescription)}\n");
                sb.Append(i < entries.Count - 1 ? "  },\n" : "  }\n");
            }
            sb.Append("]\n");
            return sb.ToString();
        }

        private static string JsonNumber(float value) => value.ToString(CultureInfo.InvariantCulture);

        private static string JsonString(string s)
        {
            var sb = new StringBuilder();
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
