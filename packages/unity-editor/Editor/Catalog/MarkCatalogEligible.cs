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
    /// on every catalog build.
    ///
    /// Selected FOLDERS get the label applied to the folder asset itself,
    /// NOT recursively to every sprite inside it - DiscoverByLabel expands
    /// a labeled folder to its sprites at catalog-build time instead. Two
    /// real reasons this isn't "just label everything inside," not just
    /// preference: (1) confirmed for real that labeling ~4,400 individual
    /// sprites took 25+ minutes and never finished (had to force-kill
    /// Unity), even after batching the AssetDatabase calls - labeling a
    /// few dozen folders instead is inherently fast, no batching needed;
    /// (2) touching thousands of sprites' own .meta files makes for a
    /// huge, hard-to-review git diff for what's conceptually a one-line
    /// policy change ("this folder is catalog content now").
    ///
    /// Selected individual SPRITE or PREFAB files (not via a folder) get
    /// labeled directly, same as before - for a one-off asset outside any
    /// labeled folder. Prefabs specifically are ONLY ever labeled this way,
    /// never swept recursively from a folder selection - a folder can hold
    /// dozens of unrelated, complex prefabs outside catalog scope (see
    /// RunCatalogBuild.cs's own long-standing comment on why
    /// -extraPrefabPaths is an explicit opt-in list, not a folder scan).
    /// Selecting the specific prefab files you want and running this
    /// command is the same level of deliberate curation -extraPrefabPaths
    /// already required, just via native multi-select instead of typing
    /// paths.
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

            var folderGuids = new HashSet<string>();
            var spriteGuids = new HashSet<string>();
            var prefabGuids = new HashSet<string>();

            foreach (var path in selectedPaths)
            {
                if (AssetDatabase.IsValidFolder(path))
                {
                    // Labeled directly, not recursed into - see class doc
                    // comment on why.
                    folderGuids.Add(AssetDatabase.AssetPathToGUID(path));
                    continue;
                }

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

            var total = folderGuids.Count + spriteGuids.Count + prefabGuids.Count;
            var processed = 0;
            var lastProgressIndex = -1;
            var cancelled = false;
            var foldersMarked = 0;
            var foldersAlready = 0;
            var spritesMarked = 0;
            var spritesAlready = 0;
            var prefabsMarked = 0;
            var prefabsAlready = 0;

            // StartAssetEditing/StopAssetEditing defers Unity's
            // import/refresh pipeline until the whole batch finishes,
            // instead of running it after every single SetLabels call.
            // Belt-and-suspenders now that folders (not their sprite
            // contents) are the common case - kept anyway since directly-
            // selected individual sprites/prefabs could still be a large
            // set in principle.
            AssetDatabase.StartAssetEditing();
            try
            {
                foldersMarked = ApplyLabel(folderGuids, out foldersAlready, ref processed, ref lastProgressIndex, total, ref cancelled);
                if (!cancelled)
                {
                    spritesMarked = ApplyLabel(spriteGuids, out spritesAlready, ref processed, ref lastProgressIndex, total, ref cancelled);
                }
                if (!cancelled)
                {
                    prefabsMarked = ApplyLabel(prefabGuids, out prefabsAlready, ref processed, ref lastProgressIndex, total, ref cancelled);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                EditorUtility.ClearProgressBar();
            }

            Debug.Log($"MarkCatalogEligible: {(cancelled ? "cancelled - " : "")}labeled {foldersMarked} folder(s) " +
                      $"({foldersAlready} already labeled), {spritesMarked} individual sprite(s) ({spritesAlready} already labeled), " +
                      $"and {prefabsMarked} prefab(s) ({prefabsAlready} already labeled) with '{Label}' " +
                      $"from {selectedPaths.Length} selected item(s) ({processed}/{total} processed).");
        }

        private static int ApplyLabel(IEnumerable<string> guids, out int alreadyMarked,
            ref int processed, ref int lastProgressIndex, int total, ref bool cancelled)
        {
            var marked = 0;
            alreadyMarked = 0;

            foreach (var guid in guids)
            {
                // Throttled, same reasoning as RunCatalogBuild's progress
                // bar - repainting the OS dialog on every single asset adds
                // up at thousands-of-assets scale.
                if (processed - lastProgressIndex >= 20 || processed == total - 1)
                {
                    lastProgressIndex = processed;
                    if (EditorUtility.DisplayCancelableProgressBar("Marking catalog-eligible assets",
                        $"{processed}/{total}", total == 0 ? 1f : (float)processed / total))
                    {
                        cancelled = true;
                        return marked;
                    }
                }
                processed++;

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
