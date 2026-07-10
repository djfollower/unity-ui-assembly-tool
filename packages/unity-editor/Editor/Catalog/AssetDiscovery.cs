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
    }
}
