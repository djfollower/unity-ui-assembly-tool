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
        /// enumerating every folder by hand. The label itself has to be
        /// applied to assets ahead of time - see `MarkCatalogEligible.cs`'s
        /// bulk-labeling utility, which is deliberately more cautious about
        /// prefabs than sprites (only directly-selected prefab files get
        /// labeled, never a whole folder's worth) - so a prefab showing up
        /// here already carries the same "explicitly opted in" trust
        /// `RunCatalogBuild.Build`'s folder-mode `-extraPrefabPaths` list
        /// has, unlike a prefab merely found sitting in a scanned folder.
        public static List<DiscoveredAsset> DiscoverByLabel(string label)
        {
            var results = new List<DiscoveredAsset>();
            var spritePaths = new HashSet<string>();

            foreach (var guid in AssetDatabase.FindAssets($"t:Sprite l:{label}"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!spritePaths.Add(path)) continue;
                results.Add(new DiscoveredAsset(path, guid, "sprite", Array.Empty<string>()));
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
