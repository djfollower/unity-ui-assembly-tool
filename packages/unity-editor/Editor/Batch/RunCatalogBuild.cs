using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UiAssemblerSlice.Editor.Assembler;
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
    ///   [-extraPrefabPaths "Assets/Prefabs/UI/A.prefab,Assets/Prefabs/UI/B.prefab"]
    ///   [-cachePath <path>] [-forceFull true] -quit
    ///
    /// Incremental rebuild (graduation-phase addition): `RenderMetadataProbe.Probe`
    /// + `RenderedThumbnail.RenderToPngBytes` are the expensive per-asset steps
    /// (texture loads, prefab instantiation, real Canvas/Camera renders) - fine
    /// at this slice's 65-entry catalog, not viable at a real project's scale
    /// (thousands of assets) if every run re-probes/re-renders everything from
    /// scratch. `AssetDatabase.GetAssetDependencyHash(path)` is Unity's own
    /// built-in primitive for exactly this: a hash that changes whenever the
    /// asset OR anything it depends on changes (so a prefab's cached entry
    /// correctly invalidates when a sprite it references is re-exported, not
    /// just when the .prefab file itself changes) - the same mechanism Unity's
    /// AssetBundle/Addressables incremental builds use internally. Keyed by
    /// asset PATH (not `id`) in a sidecar file the published catalog.json
    /// schema never sees, so a feature-folder rename only relabels a reused
    /// entry rather than forcing a re-render.
    ///
    /// Thumbnail files (graduation-phase addition, same session): `thumbnail_path`
    /// in catalog.json is a path to a PNG file (relative to catalog.json's own
    /// directory), not inline base64 - at a real project's scale, thousands of
    /// inline data URIs would bloat catalog.json into a 100s-of-MB single blob
    /// that gets fully loaded into memory (and, before this change, fully
    /// rewritten) on every run even when incremental rebuild skips the actual
    /// rendering. Written under `<catalog.json's dir>/thumbnails/<id>.png`,
    /// only when an entry is actually (re-)rendered - a cache-reused entry's
    /// thumbnail file is left untouched. Pruned the same way stale cache
    /// entries are (see `Run`'s final cleanup pass): any `.png` under
    /// `thumbnails/` not referenced by this run's entries gets deleted.
    public static class RunCatalogBuild
    {
        private const int CanonicalThumbnailSize = 256;

        public static void Run()
        {
            var args = BatchArgs.ParseArgs(Environment.GetCommandLineArgs());
            var featureFolder = args.GetValueOrDefault("-featureFolder", "Assets/Textures/UI/UI Elements");
            var outputPath = args.GetValueOrDefault("-outputPath", DefaultOutputPath());
            var cachePath = args.GetValueOrDefault("-cachePath", DefaultCachePath());
            var forceFull = args.GetValueOrDefault("-forceFull", "false") == "true";
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

            var catalogDir = Path.GetDirectoryName(outputPath) ?? ".";
            var thumbnailsDir = Path.Combine(catalogDir, "thumbnails");
            Directory.CreateDirectory(thumbnailsDir);

            var assetsThisRun = sprites.Concat(extraPrefabs).ToList();
            var cache = forceFull ? new Dictionary<string, CacheRecord>() : LoadCache(cachePath);
            var newCache = new Dictionary<string, CacheRecord>();
            var entries = new List<CatalogEntryData>();
            var reused = 0;
            var rebuilt = 0;

            foreach (var asset in assetsThisRun)
            {
                var hash = AssetDatabase.GetAssetDependencyHash(asset.Path).ToString();
                var name = Path.GetFileNameWithoutExtension(asset.Path);
                var id = $"{feature}__{name}";
                // Reused across the cache-hit and cache-miss branches: even
                // a reused entry's thumbnail file lives at this id-derived
                // path (ids are stable per asset path/feature scope, not
                // per-run), so re-stamping id/feature on a cache hit doesn't
                // orphan its own thumbnail file. Forward slash, not
                // Path.Combine - the Node side reads this literally as a
                // path fragment (path.resolve), and a Windows-built catalog
                // shouldn't write backslashes into a JSON field meant to be
                // portable.
                var thumbnailRelativePath = $"thumbnails/{id}.png";
                var thumbnailAbsolutePath = Path.Combine(catalogDir, "thumbnails", $"{id}.png");

                CatalogEntryData entry;
                // Cache hit also requires the thumbnail file to still
                // exist - guards against someone manually clearing
                // .cache/thumbnails/ without also clearing the build cache
                // (missing file -> fall through to a real rebuild, same
                // "always safe to fall back" philosophy as LoadCache below).
                if (cache.TryGetValue(asset.Path, out var cached) && cached.Hash == hash && File.Exists(thumbnailAbsolutePath))
                {
                    // Content unchanged since last build (including every
                    // dependency, e.g. a referenced sprite) - reuse the
                    // probed metadata verbatim (and its already-on-disk
                    // thumbnail file, untouched), just re-stamped with this
                    // run's id/feature in case the folder scope moved.
                    entry = cached.Entry;
                    entry.Id = id;
                    entry.Feature = feature;
                    entry.ThumbnailPath = thumbnailRelativePath;
                    reused++;
                }
                else
                {
                    var metadata = RenderMetadataProbe.Probe(asset);
                    var pngBytes = RenderedThumbnail.RenderToPngBytes(asset, metadata, CanonicalThumbnailSize);
                    File.WriteAllBytes(thumbnailAbsolutePath, pngBytes);
                    entry = new CatalogEntryData
                    {
                        Id = id,
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
                        ThumbnailPath = thumbnailRelativePath,
                        // Heuristic pending real usage data (see RenderMetadataProbe's
                        // notes on ppu_multiplier/tint - this folder has no prefabs to
                        // read a real role/interactivity signal from either).
                        Role = "base",
                        Interactive = name.ToLowerInvariant().Contains("button"),
                        // T1.7 fills this in via an LLM call on the rendered thumbnail.
                        VisualDescription = "",
                    };
                    rebuilt++;
                }

                entries.Add(entry);
                // Stored under the asset's own path, not `id` - a feature-
                // folder rename shouldn't force a re-render (see class doc
                // comment), and the reused entry above already gets this
                // run's id/feature re-stamped on top of it.
                newCache[asset.Path] = new CacheRecord { Hash = hash, Entry = entry };
            }

            PruneOrphanedThumbnails(thumbnailsDir, entries);

            File.WriteAllText(outputPath, ToJson(entries));
            SaveCache(cachePath, newCache);
            Debug.Log($"RunCatalogBuild: wrote {entries.Count} entries to {outputPath} " +
                      $"({reused} reused, {rebuilt} rebuilt; cache: {cachePath})");
        }

        // Repair utility: catalog.json can carry entries this file's own
        // discovery scan never sees (e.g. the Review Window's fallback-
        // asset importer, which appends outside `featureFolder`/
        // `extraPrefabPaths` - see HANDOFF.md's own "known, explicitly
        // out-of-scope" note on this). A plain `Run` doesn't delete such an
        // entry from catalog.json (it never touches entries outside its own
        // scan), but `PruneOrphanedThumbnails` above WILL delete its
        // thumbnail file, since that pass only knows "not referenced by
        // this run's entries" - it can't tell "out of scan scope" apart
        // from "genuinely stale." Confirmed for real: this crashed
        // `render-candidate.ts` with an uncaught ENOENT reading a missing
        // thumbnail during a later `match` run, not just a cosmetic gap.
        // Re-derives each missing thumbnail directly from catalog.json's
        // own already-recorded render metadata (no re-probing needed - the
        // values are already correct) rather than requiring the entry to
        // be back in scan scope.
        //
        // Usage: -executeMethod
        //   UiAssemblerSlice.Editor.Batch.RunCatalogBuild.RegenerateMissingThumbnails
        //   [-outputPath <catalog.json path>] -quit
        public static void RegenerateMissingThumbnails()
        {
            var args = BatchArgs.ParseArgs(Environment.GetCommandLineArgs());
            var catalogPath = args.GetValueOrDefault("-outputPath", DefaultOutputPath());
            var catalogDir = Path.GetDirectoryName(catalogPath);

            var root = (List<object>)JsonParser.Parse(File.ReadAllText(catalogPath));
            var regenerated = 0;

            foreach (var item in root)
            {
                var obj = (Dictionary<string, object>)item;
                var thumbnailRelative = (string)obj["thumbnail_path"];
                var thumbnailAbsolute = Path.GetFullPath(Path.Combine(catalogDir, thumbnailRelative));
                if (File.Exists(thumbnailAbsolute)) continue;

                var render = (Dictionary<string, object>)obj["render"];
                var border = ((List<object>)render["border"]).Select(b => (float)(double)b).ToArray();
                var nativeSize = (Dictionary<string, object>)render["native_size"];
                var metadata = new RenderMetadata(
                    (string)render["image_type"],
                    border,
                    (float)(double)render["ppu"],
                    (float)(double)render["ppu_multiplier"],
                    (float)(double)nativeSize["w"],
                    (float)(double)nativeSize["h"],
                    render.TryGetValue("tint", out var tint) ? tint as string : null);

                var asset = new DiscoveredAsset((string)obj["path"], AssetDatabase.AssetPathToGUID((string)obj["path"]), (string)obj["type"], Array.Empty<string>());
                var pngBytes = RenderedThumbnail.RenderToPngBytes(asset, metadata, CanonicalThumbnailSize);
                Directory.CreateDirectory(Path.GetDirectoryName(thumbnailAbsolute));
                File.WriteAllBytes(thumbnailAbsolute, pngBytes);
                regenerated++;
                Debug.Log($"RegenerateMissingThumbnails: regenerated {thumbnailAbsolute}");
            }

            Debug.Log($"RegenerateMissingThumbnails: {regenerated} thumbnail(s) regenerated out of {root.Count} catalog entries");
        }

        // Thumbnail files, unlike catalog/cache entries, aren't naturally
        // pruned by "just don't copy them into the new map" - they live on
        // disk independently of both JSON files. Any .png under
        // thumbnails/ not referenced by this run's entries belonged to an
        // asset that's been deleted, moved out of scope, or renamed.
        private static void PruneOrphanedThumbnails(string thumbnailsDir, List<CatalogEntryData> entries)
        {
            var referenced = new HashSet<string>(entries.Select(e => Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(thumbnailsDir) ?? ".", e.ThumbnailPath))));
            foreach (var file in Directory.GetFiles(thumbnailsDir, "*.png"))
            {
                if (!referenced.Contains(Path.GetFullPath(file)))
                {
                    File.Delete(file);
                }
            }
        }

        private static string DefaultOutputPath()
        {
            return Path.GetFullPath(Path.Combine(
                Application.dataPath, "..", "..", "unity-ui-assembly-tool", ".cache", "catalog.json"));
        }

        private static string DefaultCachePath()
        {
            return Path.GetFullPath(Path.Combine(
                Application.dataPath, "..", "..", "unity-ui-assembly-tool", ".cache", "catalog-build-cache.json"));
        }

        private static string SanitizeFeatureName(string featureFolder)
        {
            var last = featureFolder.TrimEnd('/').Split('/').Last();
            return last.Replace(" ", "");
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
            public string ThumbnailPath;
            public string Role;
            public bool Interactive;
            public string VisualDescription;
        }

        private struct CacheRecord
        {
            public string Hash;
            public CatalogEntryData Entry;
        }

        // Sidecar build cache, deliberately NOT part of catalog-entry.schema.json
        // - this is RunCatalogBuild's own bookkeeping (which source asset
        // produced which already-computed entry, and at what dependency
        // hash), never read by the Node side or any other Unity code. Missing
        // or corrupt is always safe: an empty map just means every asset
        // gets treated as changed (full rebuild), not a correctness bug.
        private static Dictionary<string, CacheRecord> LoadCache(string path)
        {
            var result = new Dictionary<string, CacheRecord>();
            if (!File.Exists(path)) return result;

            try
            {
                var root = (Dictionary<string, object>)JsonParser.Parse(File.ReadAllText(path));
                foreach (var kvp in root)
                {
                    var obj = (Dictionary<string, object>)kvp.Value;
                    result[kvp.Key] = new CacheRecord
                    {
                        Hash = (string)obj["hash"],
                        Entry = DictToEntry((Dictionary<string, object>)obj["entry"]),
                    };
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"RunCatalogBuild: could not read build cache at {path} ({e.Message}) - doing a full rebuild.");
                return new Dictionary<string, CacheRecord>();
            }

            return result;
        }

        private static void SaveCache(string path, Dictionary<string, CacheRecord> cache)
        {
            var root = new Dictionary<string, object>();
            foreach (var kvp in cache)
            {
                root[kvp.Key] = new Dictionary<string, object>
                {
                    ["hash"] = kvp.Value.Hash,
                    ["entry"] = EntryToDict(kvp.Value.Entry),
                };
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, JsonWriter.Write(root));
        }

        private static Dictionary<string, object> EntryToDict(CatalogEntryData e)
        {
            return new Dictionary<string, object>
            {
                ["id"] = e.Id,
                ["path"] = e.Path,
                ["type"] = e.Type,
                ["feature"] = e.Feature,
                ["image_type"] = e.ImageType,
                ["border"] = e.Border.Select(b => (object)(double)b).ToList(),
                ["ppu"] = (double)e.Ppu,
                ["ppu_multiplier"] = (double)e.PpuMultiplier,
                ["native_w"] = (double)e.NativeW,
                ["native_h"] = (double)e.NativeH,
                ["tint_hex"] = e.TintHex,
                ["thumbnail_path"] = e.ThumbnailPath,
                ["role"] = e.Role,
                ["interactive"] = e.Interactive,
                ["visual_description"] = e.VisualDescription,
            };
        }

        private static CatalogEntryData DictToEntry(Dictionary<string, object> d)
        {
            return new CatalogEntryData
            {
                Id = (string)d["id"],
                Path = (string)d["path"],
                Type = (string)d["type"],
                Feature = (string)d["feature"],
                ImageType = (string)d["image_type"],
                Border = ((List<object>)d["border"]).Select(b => (float)(double)b).ToArray(),
                Ppu = (float)(double)d["ppu"],
                PpuMultiplier = (float)(double)d["ppu_multiplier"],
                NativeW = (float)(double)d["native_w"],
                NativeH = (float)(double)d["native_h"],
                TintHex = d.TryGetValue("tint_hex", out var tint) ? tint as string : null,
                ThumbnailPath = (string)d["thumbnail_path"],
                Role = (string)d["role"],
                Interactive = (bool)d["interactive"],
                VisualDescription = (string)d["visual_description"],
            };
        }

        // Hand-rolled rather than JsonUtility: JsonUtility can't serialize a
        // top-level array, and can't emit JSON null for a string field
        // (tint needs to be null, not "", when untinted). Kept separate from
        // JsonWriter (used for the build cache above) - catalog.json's shape
        // is fixed/known, unlike the cache's generic dictionary shape, so
        // the existing specific encoder stays simpler than round-tripping
        // through the generic one.
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
                sb.Append($"    \"thumbnail_path\": {JsonString(e.ThumbnailPath)},\n");
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
