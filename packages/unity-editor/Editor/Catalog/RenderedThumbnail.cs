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
                    // Border values are in the sprite's own native texture
                    // pixels - Unity renders 9-slice corners at that literal
                    // pixel size regardless of the button's authored rect
                    // size (only the middle stretches). Fitting the whole
                    // authored-size render into canonicalSize applies one
                    // more uniform downscale - which must apply to the
                    // border thickness too, or corners overflow the shrunk
                    // rect and the render corrupts. Only masked before now
                    // because every earlier Sliced test case happened to
                    // render at scale exactly 1.
                    var b0 = (int)(metadata.Border[0] * scale);
                    var b1 = (int)(metadata.Border[1] * scale);
                    var b2 = (int)(metadata.Border[2] * scale);
                    var b3 = (int)(metadata.Border[3] * scale);
                    // spriteBorder order is [left, bottom, right, top];
                    // DrawTexture wants (leftBorder, rightBorder, topBorder, bottomBorder).
                    Graphics.DrawTexture(destRect, texture, new Rect(0, 0, 1, 1), b0, b2, b3, b1, tint);
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
