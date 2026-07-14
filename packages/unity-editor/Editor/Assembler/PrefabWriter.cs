using System;
using UnityEditor;
using UnityEngine;

namespace UiAssemblerSlice.Editor.Assembler
{
    /// T3.3: saves to a new .prefab path. Simplified non-destructive save -
    /// full stable-ID scheme (R8) deferred past the slice.
    public static class PrefabWriter
    {
        public static string SaveAsPrefab(GameObject root, string outputPath)
        {
            EnsureFolderExists(System.IO.Path.GetDirectoryName(outputPath).Replace('\\', '/'));

            var saved = PrefabUtility.SaveAsPrefabAsset(root, outputPath, out var success);
            if (!success || saved == null)
            {
                throw new InvalidOperationException($"PrefabWriter: failed to save prefab at {outputPath}");
            }

            // root was assembled as a loose in-memory hierarchy for this
            // batch run only (never part of a real scene) - clean it up so
            // the run stays non-destructive to whatever scene state batch
            // mode's implicit untitled scene has.
            UnityEngine.Object.DestroyImmediate(root);

            return outputPath;
        }

        // outputPath must be a Unity project-relative "Assets/..." path for
        // AssetDatabase/PrefabUtility to recognize it - raw
        // Directory.CreateDirectory + AssetDatabase.Refresh (as
        // RunCatalogBuild.cs uses for its plain-JSON .cache output) doesn't
        // reliably register a brand-new folder as an asset in time for an
        // immediate SaveAsPrefabAsset call; creating the chain via
        // AssetDatabase.CreateFolder does.
        private static void EnsureFolderExists(string assetFolderPath)
        {
            if (AssetDatabase.IsValidFolder(assetFolderPath)) return;

            var parts = assetFolderPath.Split('/');
            var current = parts[0]; // "Assets"
            for (var i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }
                current = next;
            }
        }
    }
}
