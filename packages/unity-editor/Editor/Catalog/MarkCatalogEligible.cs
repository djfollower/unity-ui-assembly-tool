using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UiAssemblerSlice.Editor.Catalog
{
    /// One-time (or as-needed) bulk-labeling utility for
    /// AssetDiscovery.DiscoverByLabel's project-wide, location-independent
    /// catalog scan. A real project's catalog-eligible sprites/prefab
    /// templates can be scattered across dozens of unrelated folders with
    /// no common parent (see HANDOFF.md) - too many to hand-enumerate as a
    /// folder list (sprites) or a path list (prefabs, "Extra prefab paths")
    /// on every catalog build. Unity's own Project window already supports
    /// multi-selecting many items at once and dragging a label onto them
    /// via the Inspector, but that only labels the selected assets
    /// themselves, not a folder's contents recursively - this fills that
    /// one gap for sprites specifically.
    ///
    /// Sprites and prefabs are deliberately NOT treated the same way here.
    /// Sprites: selecting a folder recursively labels every Sprite inside -
    /// safe, since sprites are just images. Prefabs: only directly-selected
    /// prefab files get labeled, never swept recursively from a folder
    /// selection - a folder can hold dozens of unrelated, complex prefabs
    /// outside catalog scope (see RunCatalogBuild.cs's own long-standing
    /// comment on why -extraPrefabPaths is an explicit opt-in list, not a
    /// folder scan). Selecting the specific prefab files you want and
    /// running this command is the same level of deliberate curation
    /// -extraPrefabPaths already required, just via native multi-select
    /// instead of typing paths.
    public static class MarkCatalogEligible
    {
        public const string Label = "UICatalog";

        [MenuItem("Assets/UI Assembler/Mark As Catalog-Eligible", true)]
        private static bool Validate() => Selection.objects.Length > 0;

        [MenuItem("Assets/UI Assembler/Mark As Catalog-Eligible")]
        private static void Run()
        {
            var selectedPaths = Selection.objects
                .Select(AssetDatabase.GetAssetPath)
                .Where(p => !string.IsNullOrEmpty(p))
                .ToArray();
            if (selectedPaths.Length == 0) return;

            var folderPaths = selectedPaths.Where(AssetDatabase.IsValidFolder).ToArray();
            var directAssetPaths = selectedPaths.Where(p => !AssetDatabase.IsValidFolder(p));

            var spriteGuids = new HashSet<string>();
            var prefabGuids = new HashSet<string>();

            if (folderPaths.Length > 0)
            {
                foreach (var guid in AssetDatabase.FindAssets("t:Sprite", folderPaths))
                {
                    spriteGuids.Add(guid);
                }
                // Deliberately no "t:Prefab" sweep here - see class doc
                // comment on why prefabs stay direct-selection-only.
            }
            // Individually-selected assets (not inside a selected folder) -
            // FindAssets' folder-search variant doesn't cover a bare file
            // path, so these need checking directly. This is also the ONLY
            // path that labels prefabs at all.
            foreach (var path in directAssetPaths)
            {
                var mainType = AssetDatabase.GetMainAssetTypeAtPath(path);
                if (mainType != null && typeof(Sprite).IsAssignableFrom(mainType))
                {
                    spriteGuids.Add(AssetDatabase.AssetPathToGUID(path));
                }
                else if (Path.GetExtension(path).Equals(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    prefabGuids.Add(AssetDatabase.AssetPathToGUID(path));
                }
            }

            var spritesMarked = ApplyLabel(spriteGuids, out var spritesAlready);
            var prefabsMarked = ApplyLabel(prefabGuids, out var prefabsAlready);

            Debug.Log($"MarkCatalogEligible: labeled {spritesMarked} sprite(s) ({spritesAlready} already labeled) " +
                      $"and {prefabsMarked} prefab(s) ({prefabsAlready} already labeled) with '{Label}' " +
                      $"from {selectedPaths.Length} selected item(s).");
        }

        private static int ApplyLabel(IEnumerable<string> guids, out int alreadyMarked)
        {
            var marked = 0;
            alreadyMarked = 0;

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadMainAssetAtPath(path);
                if (asset == null) continue;

                var labels = AssetDatabase.GetLabels(asset);
                if (labels.Contains(Label))
                {
                    alreadyMarked++;
                    continue;
                }

                AssetDatabase.SetLabels(asset, labels.Append(Label).ToArray());
                marked++;
            }

            return marked;
        }
    }
}
