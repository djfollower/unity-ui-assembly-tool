using System.IO;
using UnityEditor;
using UnityEngine;

namespace UiAssemblerSlice.Editor.Assembler
{
    /// Shared "write PNG bytes to a real project asset, import it through
    /// the normal Sprite pipeline" helper - factored out of
    /// NodeBuilder.SaveCompositedSprite (see its own comment for the full
    /// "why a file, not an in-memory Sprite.Create()" rationale: a saved
    /// prefab can only serialize references to real, persistent assets) so
    /// the Review Window's fallback-asset-import action (Editor/Review/) can
    /// reuse the exact same import steps instead of a second, possibly-
    /// diverging copy.
    public static class AssetImportHelpers
    {
        public static Sprite ImportPngAsSprite(string assetPath, byte[] pngBytes)
        {
            var folder = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(folder)) PrefabWriter.EnsureFolderExists(folder);

            File.WriteAllBytes(assetPath, pngBytes);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

            var importer = (TextureImporter)AssetImporter.GetAtPath(assetPath);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.SaveAndReimport();

            return AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
        }
    }
}
