using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace UiAssemblerSlice.Editor.Catalog
{
    /// T1.4: walks AssetDatabase for the chosen feature folder, emits
    /// sprite/prefab paths + basic references (which prefab uses which
    /// sprite) as an intermediate list.
    public readonly struct DiscoveredAsset
    {
        public readonly string Path;
        public readonly string Guid;
        public readonly string AssetType; // "sprite" | "prefab"
        public readonly IReadOnlyList<string> ReferencedSpritePaths; // populated for prefabs only

        public DiscoveredAsset(string path, string guid, string assetType, IReadOnlyList<string> referencedSpritePaths)
        {
            Path = path;
            Guid = guid;
            AssetType = assetType;
            ReferencedSpritePaths = referencedSpritePaths;
        }
    }

    public static class AssetDiscovery
    {
        public static List<DiscoveredAsset> DiscoverFeatureFolder(string featureFolderPath)
        {
            var results = new List<DiscoveredAsset>();
            var spritePaths = new HashSet<string>();

            foreach (var guid in AssetDatabase.FindAssets("t:Sprite", new[] { featureFolderPath }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!spritePaths.Add(path)) continue; // multi-sprite atlases can repeat a guid
                results.Add(new DiscoveredAsset(path, guid, "sprite", Array.Empty<string>()));
            }

            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { featureFolderPath }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var referencedSprites = AssetDatabase.GetDependencies(path, false)
                    .Where(spritePaths.Contains)
                    .ToList();
                results.Add(new DiscoveredAsset(path, guid, "prefab", referencedSprites));
            }

            return results;
        }

        /// Location-independent discovery: `AssetDatabase.FindAssets`'s own
        /// `l:<label>` filter, no folder restriction at all - project-wide.
        /// For a real project where catalog-eligible sprites/prefab
        /// templates are scattered across dozens of unrelated folders with
        /// no common parent (see HANDOFF.md), a folder scan
        /// (`DiscoverFeatureFolder` above) can't cover them without
        /// enumerating every folder by hand.
        ///
        /// Labels are applied ahead of time via `MarkCatalogEligible.cs`,
        /// but deliberately NOT one-sprite-at-a-time: a label on the
        /// containing FOLDER is expanded to its sprites recursively right
        /// here, at discovery time, rather than being baked into every
        /// individual sprite's own .meta file. Two real reasons, not just
        /// preference: (1) confirmed for real that labeling ~4,400
        /// individual sprites took 25+ minutes and never finished (had to
        /// force-kill Unity) even after batching the AssetDatabase calls -
        /// labeling a few dozen folders instead is inherently fast; (2)
        /// touching thousands of sprites' .meta files makes for a huge, hard
        /// -to-review git diff for what's conceptually a one-line policy
        /// change ("this folder is catalog content now"). A directly-
        /// labeled individual sprite (not via a folder) is also still
        /// supported, for the rare one-off case outside any labeled folder.
        ///
        /// Prefabs are NOT expanded from labeled folders the same way -
        /// only directly-selected/labeled prefab files ever show up here,
        /// same trust level `RunCatalogBuild.Build`'s folder-mode
        /// `-extraPrefabPaths` list already has (a folder can hold dozens of
        /// unrelated, complex prefabs outside catalog scope - see
        /// MarkCatalogEligible.cs's own doc comment).
        public static List<DiscoveredAsset> DiscoverByLabel(string label)
        {
            var results = new List<DiscoveredAsset>();
            var spritePaths = new HashSet<string>();

            void AddSprite(string guid)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (spritePaths.Add(path))
                {
                    results.Add(new DiscoveredAsset(path, guid, "sprite", Array.Empty<string>()));
                }
            }

            // Directly-labeled individual sprites.
            foreach (var guid in AssetDatabase.FindAssets($"t:Sprite l:{label}"))
            {
                AddSprite(guid);
            }

            // Labeled folders - recursively expanded to their sprites here
            // instead of at label-application time (see method doc comment
            // on why).
            var labeledFolders = AssetDatabase.FindAssets($"l:{label}")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Distinct()
                .Where(AssetDatabase.IsValidFolder);
            foreach (var folder in labeledFolders)
            {
                foreach (var guid in AssetDatabase.FindAssets("t:Sprite", new[] { folder }))
                {
                    AddSprite(guid);
                }
            }

            foreach (var guid in AssetDatabase.FindAssets($"t:Prefab l:{label}"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var referencedSprites = AssetDatabase.GetDependencies(path, false)
                    .Where(spritePaths.Contains)
                    .ToList();
                results.Add(new DiscoveredAsset(path, guid, "prefab", referencedSprites));
            }

            return results;
        }
    }
}
