using UnityEngine;

namespace UiAssemblerSlice.Editor.Assembler
{
    /// T3.2: walks confirmed match-result.json + element-tree.json, instantiates
    /// Image/TMP/prefab per element at the absolute reference-space rect (no
    /// anchoring intelligence - out of scope). Applies the R14 auto-resize
    /// (sizeDelta + multiplier) where the match result marked it safe.
    public static class NodeBuilder
    {
        public static GameObject BuildElement(Transform parent, string elementTreeJsonPath, string matchResultJsonPath)
        {
            throw new System.NotImplementedException("NodeBuilder.BuildElement: T3.2");
        }
    }
}
