using System.Collections.Generic;

namespace UiAssemblerSlice.Editor.Batch
{
    /// Shared `-key value` command-line arg parsing for -executeMethod entry
    /// points - extracted from RunCatalogBuild.cs's original private copy
    /// once RunAssemble.cs needed the identical logic (T3.3).
    public static class BatchArgs
    {
        public static Dictionary<string, string> ParseArgs(string[] args)
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
    }
}
