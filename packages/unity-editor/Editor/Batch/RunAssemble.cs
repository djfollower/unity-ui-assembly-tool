using System;
using System.Collections.Generic;
using System.IO;
using UiAssemblerSlice.Editor.Assembler;
using UnityEngine;

namespace UiAssemblerSlice.Editor.Batch
{
    /// T3.3: -executeMethod entry point. Wraps CanvasScaffold + NodeBuilder +
    /// PrefabWriter.
    ///
    /// Usage: Unity -batchmode -executeMethod UiAssemblerSlice.Editor.Batch.RunAssemble.Run
    ///   -elementTreePath <path> -matchResultPath <path> [-catalogPath <path>]
    ///   [-outputPath <Assets/... .prefab path>] -quit
    public static class RunAssemble
    {
        public static void Run()
        {
            var args = BatchArgs.ParseArgs(Environment.GetCommandLineArgs());
            var elementTreePath = RequireArg(args, "-elementTreePath");
            var matchResultPath = RequireArg(args, "-matchResultPath");
            var catalogPath = args.GetValueOrDefault("-catalogPath", DefaultCatalogPath());

            var elementTree = AssemblerJson.LoadElementTree(elementTreePath);
            var matchResults = AssemblerJson.LoadMatchResults(matchResultPath);
            var catalog = AssemblerJson.LoadCatalog(catalogPath);

            var outputPath = args.GetValueOrDefault("-outputPath", DefaultOutputPath(elementTree.FrameId));

            Debug.Log($"RunAssemble: assembling frame {elementTree.FrameId} -> {outputPath}");

            var canvas = CanvasScaffold.CreateRootCanvas(elementTree);
            var root = NodeBuilder.BuildElement(canvas.transform, elementTree, matchResults, catalog);
            var saved = PrefabWriter.SaveAsPrefab(root, outputPath);

            Debug.Log($"RunAssemble: wrote {saved}");
        }

        private static string RequireArg(Dictionary<string, string> args, string name)
        {
            if (!args.TryGetValue(name, out var value))
            {
                throw new ArgumentException($"RunAssemble: missing required argument {name}");
            }
            return value;
        }

        // catalog.json/element-tree.json/match-result.json are plain files
        // read via File.ReadAllText, so - unlike outputPath below - this
        // stays an OS-absolute path, same convention as RunCatalogBuild.cs's
        // own -outputPath default.
        private static string DefaultCatalogPath()
        {
            return Path.GetFullPath(Path.Combine(
                Application.dataPath, "..", "..", "unity-ui-assembly-tool", ".cache", "catalog.json"));
        }

        // Deliberately a Unity-project-relative "Assets/..." path, not an OS
        // path - PrefabWriter saves it via AssetDatabase/PrefabUtility,
        // which require project-relative paths.
        // internal, not private: ReviewWindow (Editor/Review/) reuses this
        // exact same Assets/... path logic for its in-process "Save &
        // Assemble" action, rather than re-deriving it a second time.
        internal static string DefaultOutputPath(string frameId)
        {
            var sanitized = frameId.Replace(':', '_');
            return $"Assets/_Generated/UIAssembler/{sanitized}.prefab";
        }
    }
}
