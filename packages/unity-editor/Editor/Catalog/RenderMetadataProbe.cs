using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace UiAssemblerSlice.Editor.Catalog
{
    /// T1.5: per asset, reads Image.type, sprite.border, PPU, multiplier,
    /// tint (R14). Highest-craft piece of Unity code in the slice. Runs as a
    /// full synchronous scan; caching/incrementality is a post-slice
    /// scale-up.
    ///
    /// image_type/border/ppu are genuine properties of the Sprite asset
    /// itself and are read directly. For a bare sprite (no prefab wrapping
    /// it), ppu_multiplier/tint default to 1.0/null since there's no Image
    /// component to read them from. For a prefab, all of it - including the
    /// resolved size, which is authoritative over the underlying sprite's
    /// native size for prefab variants with their own RectTransform
    /// overrides - comes from the prefab's own (first) Image component.
    public readonly struct RenderMetadata
    {
        public readonly string ImageType; // "Simple" | "Sliced" | "Tiled" | "Filled"
        public readonly float[] Border; // [left, bottom, right, top]
        public readonly float Ppu;
        public readonly float PpuMultiplier;
        public readonly float NativeWidth;
        public readonly float NativeHeight;
        public readonly string TintHex; // null if untinted / undeterminable

        public RenderMetadata(
            string imageType,
            float[] border,
            float ppu,
            float ppuMultiplier,
            float nativeWidth,
            float nativeHeight,
            string tintHex)
        {
            ImageType = imageType;
            Border = border;
            Ppu = ppu;
            PpuMultiplier = ppuMultiplier;
            NativeWidth = nativeWidth;
            NativeHeight = nativeHeight;
            TintHex = tintHex;
        }
    }

    public static class RenderMetadataProbe
    {
        public static RenderMetadata Probe(DiscoveredAsset asset)
        {
            return asset.AssetType == "prefab" ? ProbePrefab(asset) : ProbeSprite(asset);
        }

        private static RenderMetadata ProbeSprite(DiscoveredAsset asset)
        {
            var importer = AssetImporter.GetAtPath(asset.Path) as TextureImporter;
            if (importer == null)
            {
                throw new InvalidOperationException($"RenderMetadataProbe.Probe: no TextureImporter at {asset.Path}");
            }

            var border = importer.spriteBorder; // x=left, y=bottom, z=right, w=top, in source texture pixels
            var hasBorder = border != Vector4.zero;

            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(asset.Path);
            if (sprite == null)
            {
                throw new InvalidOperationException($"RenderMetadataProbe.Probe: no Sprite sub-asset at {asset.Path}");
            }

            return new RenderMetadata(
                imageType: hasBorder ? "Sliced" : "Simple",
                border: new[] { border.x, border.y, border.z, border.w },
                ppu: importer.spritePixelsPerUnit,
                ppuMultiplier: 1f,
                nativeWidth: sprite.rect.width,
                nativeHeight: sprite.rect.height,
                tintHex: null);
        }

        /// Composite button-style prefabs commonly have more than one Image,
        /// and picking the wrong one is easy:
        ///  - Several images for different states (normal/pressed/disabled),
        ///    with only one active GameObject at a time - inactive siblings
        ///    must be excluded, not just "the first Image found."
        ///  - A Button's targetGraphic is sometimes an invisible full-size
        ///    hit-box (fully transparent sprite, or color.a == 0) used only
        ///    for raycasting, while the real visible artwork is a sibling
        ///    Image that ISN'T the target graphic - the inverse of the
        ///    pattern this method originally assumed (confirmed correct on
        ///    ButtonFrameTint, where targetGraphic WAS the visible tinted
        ///    surface - both patterns are real, so targetGraphic is a good
        ///    first guess, not a reliable rule on its own).
        /// So: gather every Image that is actually visually contributing
        /// (active in hierarchy, enabled, has a sprite, non-zero alpha),
        /// prefer targetGraphic if it's among them, otherwise fall back to
        /// the largest by rendered area - the main background art is
        /// typically the biggest layer, with icons/decorations smaller and
        /// on top.
        public static Image ResolveMainImage(GameObject go)
        {
            var visible = go.GetComponentsInChildren<Image>(true)
                .Where(img => IsVisuallyContributing(img, go.transform))
                .ToList();

            if (visible.Count == 0)
            {
                return null;
            }

            var selectable = go.GetComponentInChildren<Selectable>(true);
            if (selectable != null && selectable.targetGraphic is Image targetImage && visible.Contains(targetImage))
            {
                return targetImage;
            }

            return visible.OrderByDescending(img => img.rectTransform.rect.width * img.rectTransform.rect.height).First();
        }

        private static bool IsVisuallyContributing(Image img, Transform prefabRoot) =>
            img.enabled && img.sprite != null && img.color.a > 0.01f && IsActiveUpToRoot(img.transform, prefabRoot);

        // GameObject.activeInHierarchy is unreliable here: it reports false
        // universally for a prefab ASSET loaded via AssetDatabase and never
        // instantiated into a scene (there's no live "hierarchy" for it to
        // be active IN), even when every activeSelf up the chain is true.
        // Confirmed on this project's own ButtonFrame.prefab - its root has
        // activeSelf=true, activeInHierarchy=false. Walk activeSelf
        // ourselves instead, up to (and including) the prefab root.
        private static bool IsActiveUpToRoot(Transform t, Transform root)
        {
            for (var current = t; current != null; current = current.parent)
            {
                if (!current.gameObject.activeSelf) return false;
                if (current == root) return true;
            }
            return true; // walked off the top without finding root - treat as active
        }

        private static RenderMetadata ProbePrefab(DiscoveredAsset asset)
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(asset.Path);
            if (go == null)
            {
                throw new InvalidOperationException($"RenderMetadataProbe.Probe: could not load prefab at {asset.Path}");
            }

            var image = ResolveMainImage(go);
            if (image == null || image.sprite == null)
            {
                throw new InvalidOperationException(
                    $"RenderMetadataProbe.Probe: prefab {asset.Path} has no Image component with a sprite assigned");
            }

            var border = image.sprite.border; // resolved on the runtime Sprite - correct even for atlas-packed sprites
            var rect = image.rectTransform.rect; // authored/resolved size, authoritative for prefab variants over the sprite's own native size

            // Stretch-anchored RectTransforms (e.g. a full-screen scrim, with
            // anchorMin (0,0) - anchorMax (1,1)) resolve to a 0-size rect
            // when read from a prefab ASSET outside any parented Canvas -
            // there's no parent to stretch relative to. Their "native size"
            // is genuinely context-dependent (they're designed to fill
            // whatever they're placed in), so fall back to the sprite's own
            // pixel size as a non-zero, honest-if-imperfect proxy rather
            // than propagating a 0x0 that would break downstream scale math.
            var isDegenerate = rect.width < 1f || rect.height < 1f;
            var nativeWidth = isDegenerate ? image.sprite.rect.width : rect.width;
            var nativeHeight = isDegenerate ? image.sprite.rect.height : rect.height;
            if (isDegenerate)
            {
                Debug.LogWarning(
                    $"RenderMetadataProbe.Probe: {asset.Path}'s Image resolved to a 0-size RectTransform " +
                    "(likely stretch-anchored to a parent Canvas that doesn't exist for an un-instantiated " +
                    "prefab asset) - falling back to the sprite's own native pixel size.");
            }

            return new RenderMetadata(
                imageType: image.type.ToString(), // Simple | Sliced | Tiled | Filled - matches the schema enum exactly
                border: new[] { border.x, border.y, border.z, border.w },
                ppu: image.sprite.pixelsPerUnit,
                ppuMultiplier: image.pixelsPerUnitMultiplier,
                nativeWidth: nativeWidth,
                nativeHeight: nativeHeight,
                tintHex: IsWhite(image.color) ? null : "#" + ColorUtility.ToHtmlStringRGB(image.color));
        }

        private static bool IsWhite(Color c) =>
            Mathf.Approximately(c.r, 1f) && Mathf.Approximately(c.g, 1f) &&
            Mathf.Approximately(c.b, 1f) && Mathf.Approximately(c.a, 1f);
    }
}
