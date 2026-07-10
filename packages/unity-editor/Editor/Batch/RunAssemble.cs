using System;

namespace UiAssemblerSlice.Editor.Batch
{
    /// T3.3: -executeMethod entry point. Wraps CanvasScaffold + NodeBuilder +
    /// PrefabWriter.
    ///
    /// Usage: Unity -batchmode -executeMethod UiAssemblerSlice.Editor.Batch.RunAssemble.Run
    ///   -elementTreePath <path> -matchResultPath <path> -outputPath <path> -quit
    public static class RunAssemble
    {
        public static void Run()
        {
            // TODO(T3.3): parse -elementTreePath / -matchResultPath / -outputPath
            // from Environment.GetCommandLineArgs().
            throw new NotImplementedException("RunAssemble.Run: T3.3");
        }
    }
}
