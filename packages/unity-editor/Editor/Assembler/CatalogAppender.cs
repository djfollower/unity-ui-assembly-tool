using System.Collections.Generic;
using System.IO;
using UiAssemblerSlice.Editor.Catalog;

namespace UiAssemblerSlice.Editor.Assembler
{
    /// Appends one new entry to an existing catalog.json - no writer for
    /// this existed before (RunCatalogBuild.cs's own ToJson is private and
    /// keyed to its own local entry struct, not reusable here). Used by the
    /// Review Window's fallback-asset-import action (Editor/Review/) to
    /// register a captured Combine-group image as a real, reusable catalog
    /// entry - reuses AssemblerJson's JsonParser/JsonWriter, the same
    /// round-trip pattern RunCatalogBuild.cs's own build-cache load/save
    /// already uses.
    public static class CatalogAppender
    {
        public static void AppendEntry(string catalogPath, Dictionary<string, object> entry)
        {
            var root = (List<object>)JsonParser.Parse(File.ReadAllText(catalogPath));
            root.Add(entry);
            File.WriteAllText(catalogPath, JsonWriter.Write(root));
        }

        // Builds one catalog-entry.schema.json-shaped dictionary for a
        // freshly-imported sprite asset - same required fields
        // RunCatalogBuild.cs's ToJson emits for a real discovered asset, by
        // hand since that encoder isn't reusable here (see class comment).
        // thumbnailPath is expected already-relative to catalog.json's own
        // directory, same convention RunCatalogBuild.cs uses
        // ("thumbnails/<id>.png").
        public static Dictionary<string, object> BuildSpriteEntry(
            string id,
            string assetPath,
            string feature,
            RenderMetadata metadata,
            string thumbnailPath)
        {
            return new Dictionary<string, object>
            {
                ["id"] = id,
                ["path"] = assetPath,
                ["type"] = "sprite",
                ["feature"] = feature,
                ["render"] = new Dictionary<string, object>
                {
                    ["image_type"] = metadata.ImageType,
                    ["border"] = new List<object>
                    {
                        (double)metadata.Border[0],
                        (double)metadata.Border[1],
                        (double)metadata.Border[2],
                        (double)metadata.Border[3],
                    },
                    ["ppu"] = (double)metadata.Ppu,
                    ["ppu_multiplier"] = (double)metadata.PpuMultiplier,
                    ["native_size"] = new Dictionary<string, object>
                    {
                        ["w"] = (double)metadata.NativeWidth,
                        ["h"] = (double)metadata.NativeHeight,
                    },
                    ["tint"] = metadata.TintHex,
                },
                ["thumbnail_path"] = thumbnailPath,
                ["role"] = "base",
                ["interactive"] = false,
                ["visual_description"] = "",
            };
        }
    }
}
