using System;
using UnityEditor;
using UnityEngine;

namespace UiAssemblerSlice.Editor.Catalog
{
    /// T1.5: renders 9-sliced/tinted sprites to a canonical size for the
    /// thumbnail (R14) - the thumbnail is the RENDERED asset, not the raw
    /// texture, so downstream visual comparisons see what the player sees.
    ///
    /// Draws directly into a RenderTexture via GL.LoadPixelMatrix +
    /// Graphics.DrawTexture's built-in 9-slice border overload - no
    /// GameObjects/Camera/SpriteRenderer needed. Two earlier approaches were
    /// tried and rejected:
    ///  - Canvas + UI.Image (ScreenSpaceCamera): Canvas's automatic
    ///    per-frame camera fitting fought a manually configured camera and
    ///    produced a flat, cropped fill.
    ///  - SpriteRenderer.drawMode = Sliced: requires the source sprite's
    ///    "Mesh Type" import setting to be Full Rect; these sprites use the
    ///    (correct, for their actual usage) default Tight mesh type, and
    ///    this folder contains a SpriteAtlas that toggling+reimporting
    ///    import settings risked disturbing. Graphics.DrawTexture reads the
    ///    raw Texture2D directly and is unaffected by mesh type.
    /// Also confirmed batch mode must run WITHOUT -nographics - GPU
    /// Blit/RenderTexture returns flat/uninitialized output under
    /// -nographics on this machine (see scripts/logs/t1.5-nographics-probe.log
    /// vs t1.5-graphics-probe.log).
    ///
    /// button_x/button_settings thumbnail bug, resolved: the border ints
    /// passed to Graphics.DrawTexture were being multiplied by `scale`
    /// (the canonicalSize/nativeSize upscale factor) on the theory that
    /// they're destination-space pixels - confirmed WRONG via an isolated
    /// synthetic-texture test (see SmokeTest.ProbeDrawTextureBorderSemantics
    /// and HANDOFF.md): they're literal, UNSCALED source-texture pixel
    /// counts. Multiplying by scale (>1 whenever a sprite needs upscaling
    /// to fill the canonical thumbnail) inflated the border past the
    /// source texture's own bounds, and DrawTexture tiles/repeats the
    /// source rather than erroring - exactly the "duplicated" look. It
    /// went unnoticed for other assets needing the same upscale (e.g.
    /// button_frame_x) only because their art is simple/mostly-flat-colored
    /// enough that tiling isn't visually obvious. Two earlier fix attempts
    /// this session chased a different, adjacent theory (SpriteAtlas
    /// packing/trim requiring the source texture to be resolved via
    /// Sprite.texture + isolated into its own standalone texture) before
    /// this one was found - that approach turned out unnecessary for this
    /// bug and caused its own regression (button_frame_blue rendering as a
    /// flat, structureless fill), so it was reverted; loading the raw
    /// Texture2D by path, as this file always did, is sufficient once the
    /// border scaling itself is correct.
    public static class RenderedThumbnail
    {
        private static Texture2D ResolveSourceTexture(DiscoveredAsset asset)
        {
            if (asset.AssetType != "prefab")
            {
                return AssetDatabase.LoadAssetAtPath<Texture2D>(asset.Path);
            }

            var go = AssetDatabase.LoadAssetAtPath<GameObject>(asset.Path);
            var image = go != null ? RenderMetadataProbe.ResolveMainImage(go) : null;
            if (image?.sprite == null)
            {
                return null;
            }
            var spritePath = AssetDatabase.GetAssetPath(image.sprite);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(spritePath);
        }

        public static string RenderToBase64Png(DiscoveredAsset asset, RenderMetadata metadata, int canonicalSize)
        {
            var texture = ResolveSourceTexture(asset);
            if (texture == null)
            {
                throw new InvalidOperationException($"RenderedThumbnail: could not resolve a source texture for {asset.Path}");
            }

            var scale = Mathf.Min(canonicalSize / metadata.NativeWidth, canonicalSize / metadata.NativeHeight);
            var w = metadata.NativeWidth * scale;
            var h = metadata.NativeHeight * scale;
            var destRect = new Rect((canonicalSize - w) / 2f, (canonicalSize - h) / 2f, w, h);

            var tint = Color.white;
            if (!string.IsNullOrEmpty(metadata.TintHex) && ColorUtility.TryParseHtmlString(metadata.TintHex, out var parsedTint))
            {
                tint = parsedTint;
            }

            RenderTexture rt = null;
            Texture2D readable = null;
            var prevActive = RenderTexture.active;

            try
            {
                rt = RenderTexture.GetTemporary(canonicalSize, canonicalSize, 0, RenderTextureFormat.ARGB32);
                RenderTexture.active = rt;
                GL.Clear(true, true, new Color(0, 0, 0, 0));
                GL.PushMatrix();
                GL.LoadPixelMatrix(0, canonicalSize, canonicalSize, 0);

                if (metadata.ImageType == "Sliced")
                {
                    // Border ints are literal source-texture pixels, NOT
                    // scaled by `scale` - see the class doc comment above.
                    // But corners preserving their literal pixel size means
                    // a border sum CAN exceed destRect's own size on an
                    // axis when the asset needs significant downscaling
                    // (large native size relative to canonicalSize, e.g.
                    // UIElements__ButtonFrameTint at 531x232 -> a 256-wide,
                    // ~112-tall destRect, where the unscaled top+bottom
                    // border sum is 229px against that 112px height) -
                    // Graphics.DrawTexture doesn't clamp this itself and
                    // corrupts the render, same failure family as the
                    // upscale bug this file just fixed, just the opposite
                    // direction. Real Unity UI Images handle this by
                    // shrinking the border proportionally once it would
                    // exceed the element's own box - mirror that here.
                    var b0 = metadata.Border[0];
                    var b1 = metadata.Border[1];
                    var b2 = metadata.Border[2];
                    var b3 = metadata.Border[3];
                    if (b0 + b2 > destRect.width && b0 + b2 > 0)
                    {
                        var horizontalClamp = destRect.width / (b0 + b2);
                        b0 *= horizontalClamp;
                        b2 *= horizontalClamp;
                    }
                    if (b1 + b3 > destRect.height && b1 + b3 > 0)
                    {
                        var verticalClamp = destRect.height / (b1 + b3);
                        b1 *= verticalClamp;
                        b3 *= verticalClamp;
                    }
                    // spriteBorder order is [left, bottom, right, top];
                    // DrawTexture wants (leftBorder, rightBorder, topBorder, bottomBorder).
                    Graphics.DrawTexture(destRect, texture, new Rect(0, 0, 1, 1), (int)b0, (int)b2, (int)b3, (int)b1, tint);
                }
                else
                {
                    Graphics.DrawTexture(destRect, texture, new Rect(0, 0, 1, 1), 0, 0, 0, 0, tint);
                }

                GL.PopMatrix();

                readable = new Texture2D(canonicalSize, canonicalSize, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, canonicalSize, canonicalSize), 0, 0);
                readable.Apply();

                var png = readable.EncodeToPNG();
                return "data:image/png;base64," + Convert.ToBase64String(png);
            }
            finally
            {
                RenderTexture.active = prevActive;
                if (readable != null) UnityEngine.Object.DestroyImmediate(readable);
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
            }
        }
    }
}
