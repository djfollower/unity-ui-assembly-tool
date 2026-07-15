using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace UiAssemblerSlice.Editor.Catalog
{
    /// T1.5: renders a canonical-size thumbnail (R14) - the RENDERED asset,
    /// not the raw texture, so downstream visual comparisons see what the
    /// player sees. For a bare sprite this is one 9-slice/tint composite.
    /// For a prefab, a real catalog entry can be a composite of several
    /// Images and Text (e.g. `ButtonFrame`: a larger blue frame Image
    /// behind a green button Image, an icon placeholder Image, and a "100"
    /// TMP label) - the thumbnail renders every active/visible one of them,
    /// not just the single "main image" `RenderMetadataProbe.ResolveMainImage`
    /// resolves for matching purposes (that method answers a different
    /// question: "which one Image represents this entry for scoring", not
    /// "what does this asset look like").
    ///
    /// Sliced sprites are composited by hand via `Graphics.DrawTexture`'s
    /// border overload, reading the raw loose Texture2D by path - never via
    /// `UnityEngine.UI.Image`'s own built-in Sliced rendering. Same proven
    /// convention `NodeBuilder.RenderSlicedToTexture` uses for the real,
    /// live-validated assembler (see its own comment: `UI.Image`'s built-in
    /// Sliced mesh generation was found to render these specific
    /// Tight-mesh-type/SpriteAtlasV2-packed sprites as a flat, unrounded
    /// rectangle once resized away from native size - a real, confirmed bug
    /// in Image's own mesh generation for this project's assets, not a
    /// theory). Border ints are literal, UNSCALED source-texture pixel
    /// counts (confirmed via `SmokeTest.ProbeDrawTextureBorderSemantics`),
    /// passed through UNCLAMPED, and only ever composited at a size at
    /// least as large as the sprite's own native/authored size - border
    /// proportions are only valid relative to that size (what they were
    /// authored against in the Sprite Editor); compositing directly at a
    /// smaller destination corrupts the render regardless of clamping
    /// (confirmed empirically - see HANDOFF.md "Thumbnail rendering bug,
    /// part 2"). Any further size change (fitting the canonical square,
    /// or the prefab's own native size not matching canonicalSize) is
    /// always a separate, plain bilinear resample of the finished bitmap -
    /// never a second pass through the border logic.
    ///
    /// The prefab path renders through a REAL (but isolated, disposable)
    /// Canvas + Camera + the prefab's own instantiated hierarchy, so
    /// Text/TMP and correctly-positioned/z-ordered sibling Images render
    /// exactly as Unity's real UI system lays them out - only each
    /// contributing Sliced Image's sprite is swapped for a pre-composited
    /// Simple one first (same swap-before-render recipe
    /// `NodeBuilder.ResizeMainImageToFill` already uses), to route around
    /// the same `UI.Image` Sliced bug.
    public static class RenderedThumbnail
    {
        public static string RenderToBase64Png(DiscoveredAsset asset, RenderMetadata metadata, int canonicalSize)
        {
            var fitted = asset.AssetType == "prefab"
                ? RenderPrefabHierarchy(asset, metadata, canonicalSize)
                : RenderSprite(asset, metadata, canonicalSize);

            try
            {
                var canvas = new Texture2D(canonicalSize, canonicalSize, TextureFormat.RGBA32, false);
                try
                {
                    var clear = new Color32[canonicalSize * canonicalSize];
                    canvas.SetPixels32(clear);
                    canvas.SetPixels((canonicalSize - fitted.width) / 2, (canonicalSize - fitted.height) / 2,
                        fitted.width, fitted.height, fitted.GetPixels());
                    canvas.Apply();

                    var png = canvas.EncodeToPNG();
                    return "data:image/png;base64," + Convert.ToBase64String(png);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(canvas);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(fitted);
            }
        }

        // Returns a bitmap sized to fit within canonicalSize (native aspect
        // preserved, not yet centered/padded into the square canvas).
        private static Texture2D RenderSprite(DiscoveredAsset asset, RenderMetadata metadata, int canonicalSize)
        {
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(asset.Path);
            if (texture == null)
            {
                throw new InvalidOperationException($"RenderedThumbnail: could not resolve a source texture for {asset.Path}");
            }

            var tint = Color.white;
            if (!string.IsNullOrEmpty(metadata.TintHex) && ColorUtility.TryParseHtmlString(metadata.TintHex, out var parsedTint))
            {
                tint = parsedTint;
            }

            var scale = Mathf.Min(canonicalSize / metadata.NativeWidth, canonicalSize / metadata.NativeHeight);
            var w = Mathf.Max(1, Mathf.RoundToInt(metadata.NativeWidth * scale));
            var h = Mathf.Max(1, Mathf.RoundToInt(metadata.NativeHeight * scale));
            var sliced = metadata.ImageType == "Sliced";

            if (scale >= 1f)
            {
                return CompositeAtSize(texture, sliced, metadata.Border, tint, w, h);
            }

            var natW = Mathf.Max(1, Mathf.RoundToInt(metadata.NativeWidth));
            var natH = Mathf.Max(1, Mathf.RoundToInt(metadata.NativeHeight));
            var native = CompositeAtSize(texture, sliced, metadata.Border, tint, natW, natH);
            try
            {
                return ResampleBilinear(native, w, h);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(native);
            }
        }

        // Instantiates the prefab's own hierarchy, pre-composites every
        // contributing Sliced Image's sprite, then renders the whole thing
        // through a real Canvas + Camera at native scale before resampling
        // to fit canonicalSize.
        private static Texture2D RenderPrefabHierarchy(DiscoveredAsset asset, RenderMetadata metadata, int canonicalSize)
        {
            var prefabSource = AssetDatabase.LoadAssetAtPath<GameObject>(asset.Path);
            if (prefabSource == null)
            {
                throw new InvalidOperationException($"RenderedThumbnail: could not load prefab at {asset.Path}");
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefabSource);
            var tempAssets = new List<UnityEngine.Object>();
            GameObject canvasGo = null;
            GameObject camGo = null;
            RenderTexture rt = null;
            var prevActive = RenderTexture.active;

            try
            {
                canvasGo = new GameObject("ThumbnailCanvas", typeof(Canvas), typeof(CanvasScaler));
                instance.transform.SetParent(canvasGo.transform, false);

                var rootRt = instance.GetComponent<RectTransform>();
                // Stretch-anchored roots (e.g. Scrim, designed to fill
                // whatever screen it's placed in - anchorMin != anchorMax
                // on some axis) have no size of their own: `rect.size`
                // there is derived from the PARENT's size, and our own
                // synthetic canvasGo has an arbitrary/meaningless default
                // RectTransform size, so reading rect.width/height BEFORE
                // reparenting fixes anything is not trustworthy at all
                // (not just "sometimes reads as 0" - it can read as any
                // arbitrary non-zero number, e.g. Unity's default 100x100,
                // and looks superficially valid). Detect stretch directly
                // from the anchors instead of inferring it from the
                // resolved rect, and fall back to RenderMetadataProbe's
                // already-resolved NativeWidth/NativeHeight (same case
                // ProbePrefab documents its own fallback for) rather than
                // re-deriving a second, less reliable one here.
                var wasStretched = !Mathf.Approximately(rootRt.anchorMin.x, rootRt.anchorMax.x) ||
                                    !Mathf.Approximately(rootRt.anchorMin.y, rootRt.anchorMax.y);
                var natSizeW = wasStretched ? metadata.NativeWidth : rootRt.rect.width;
                var natSizeH = wasStretched ? metadata.NativeHeight : rootRt.rect.height;

                rootRt.anchorMin = new Vector2(0.5f, 0.5f);
                rootRt.anchorMax = new Vector2(0.5f, 0.5f);
                rootRt.pivot = new Vector2(0.5f, 0.5f);
                rootRt.anchoredPosition = Vector2.zero;
                // Always re-assign explicitly, whether stretched or not:
                // once anchors are fixed-point, rect.size IS sizeDelta -
                // nothing else determines it from here on.
                rootRt.sizeDelta = new Vector2(natSizeW, natSizeH);
                var nativeW = Mathf.Max(1, Mathf.RoundToInt(natSizeW));
                var nativeH = Mathf.Max(1, Mathf.RoundToInt(natSizeH));

                // Swap every visually-contributing Sliced Image's sprite
                // for a pre-composited Simple one at ITS OWN authored rect
                // size before the real Canvas ever renders it - see the
                // class doc comment for why UI.Image's built-in Sliced path
                // can't be trusted for this project's assets. Untinted
                // (Color.white): the Image's own `color` is left untouched
                // and still applies at render time, same as
                // NodeBuilder.ResizeMainImageToFill.
                foreach (var img in instance.GetComponentsInChildren<Image>(true))
                {
                    if (!img.gameObject.activeInHierarchy || !img.enabled || img.color.a <= 0.01f) continue;
                    if (img.type != Image.Type.Sliced || img.sprite == null) continue;

                    var spritePath = AssetDatabase.GetAssetPath(img.sprite);
                    var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(spritePath);
                    var border = img.sprite.border;
                    var tw = Mathf.Max(1, Mathf.RoundToInt(img.rectTransform.rect.width));
                    var th = Mathf.Max(1, Mathf.RoundToInt(img.rectTransform.rect.height));
                    var composited = CompositeAtSize(texture, true, new[] { border.x, border.y, border.z, border.w }, Color.white, tw, th);
                    var sprite = Sprite.Create(composited, new Rect(0, 0, tw, th), new Vector2(0.5f, 0.5f));
                    tempAssets.Add(sprite);
                    tempAssets.Add(composited);
                    img.sprite = sprite;
                    img.type = Image.Type.Simple;
                }

                // Layout Group-driven children (e.g. ButtonFrame's icon +
                // "100" label, arranged by a HorizontalLayoutGroup on
                // ButtonContinue) only get their real positions from a
                // layout pass - which normally runs lazily, on the next UI
                // update, not synchronously on instantiation. Whatever
                // anchoredPosition happens to already be serialized in the
                // prefab is what renders otherwise (observed: ButtonFrame's
                // serialized icon position pre-dates its layout group and
                // sits centered/overlapping the label; ButtonFrameTint's
                // happens to already match a rebuilt layout, purely by
                // coincidence of when it was last saved in the Editor).
                // Force one explicitly so both render correctly regardless
                // of stale serialized state.
                LayoutRebuilder.ForceRebuildLayoutImmediate(rootRt);

                camGo = new GameObject("ThumbnailCam", typeof(Camera));
                var cam = camGo.GetComponent<Camera>();
                cam.orthographic = true;
                cam.orthographicSize = nativeH / 2f;
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = 100f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0, 0, 0, 0);
                cam.aspect = (float)nativeW / nativeH;

                rt = RenderTexture.GetTemporary(nativeW, nativeH, 24, RenderTextureFormat.ARGB32);
                // Assigning targetTexture before the Canvas is fully wired
                // up matters - a Screen Space - Camera Canvas sizes its own
                // screen-space geometry from the camera's actual render
                // target dimensions at that point (see
                // SmokeTest.CaptureCatalogEntryRender's identical comment).
                cam.targetTexture = rt;

                var canvas = canvasGo.GetComponent<Canvas>();
                var scaler = canvasGo.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
                scaler.scaleFactor = 1;
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = cam;
                canvas.planeDistance = 10;

                Canvas.ForceUpdateCanvases();
                cam.Render();

                RenderTexture.active = rt;
                var native = new Texture2D(nativeW, nativeH, TextureFormat.RGBA32, false);
                native.ReadPixels(new Rect(0, 0, nativeW, nativeH), 0, 0);
                native.Apply();

                var scale = Mathf.Min((float)canonicalSize / nativeW, (float)canonicalSize / nativeH);
                var w = Mathf.Max(1, Mathf.RoundToInt(nativeW * scale));
                var h = Mathf.Max(1, Mathf.RoundToInt(nativeH * scale));
                try
                {
                    return ResampleBilinear(native, w, h);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(native);
                }
            }
            finally
            {
                RenderTexture.active = prevActive;
                if (rt != null)
                {
                    var cam = camGo != null ? camGo.GetComponent<Camera>() : null;
                    if (cam != null) cam.targetTexture = null;
                    RenderTexture.ReleaseTemporary(rt);
                }
                if (camGo != null) UnityEngine.Object.DestroyImmediate(camGo);
                if (canvasGo != null) UnityEngine.Object.DestroyImmediate(canvasGo); // also destroys the reparented prefab instance
                foreach (var tempAsset in tempAssets) UnityEngine.Object.DestroyImmediate(tempAsset);
            }
        }

        // Composites at EXACTLY (w, h) - the render fills its entire target
        // with no offset/padding, matching `NodeBuilder.RenderSlicedToTexture`'s
        // proven usage exactly (`Graphics.DrawTexture`'s border handling
        // was confirmed to misbehave when drawn into an inset destRect
        // within a larger canvas - see the class doc comment).
        private static Texture2D CompositeAtSize(Texture2D texture, bool sliced, float[] border, Color tint, int w, int h)
        {
            RenderTexture rt = null;
            var prevActive = RenderTexture.active;
            try
            {
                rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
                RenderTexture.active = rt;
                GL.Clear(true, true, new Color(0, 0, 0, 0));
                GL.PushMatrix();
                GL.LoadPixelMatrix(0, w, h, 0);

                if (sliced)
                {
                    // border order is [left, bottom, right, top];
                    // DrawTexture wants (leftBorder, rightBorder, topBorder, bottomBorder).
                    // Literal source-texture pixels, passed through
                    // unclamped - see the class doc comment above.
                    Graphics.DrawTexture(new Rect(0, 0, w, h), texture, new Rect(0, 0, 1, 1),
                        (int)border[0], (int)border[2], (int)border[3], (int)border[1], tint);
                }
                else
                {
                    Graphics.DrawTexture(new Rect(0, 0, w, h), texture, new Rect(0, 0, 1, 1), 0, 0, 0, 0, tint);
                }

                GL.PopMatrix();

                var readable = new Texture2D(w, h, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                readable.Apply();
                return readable;
            }
            finally
            {
                RenderTexture.active = prevActive;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
            }
        }

        // Plain bilinear image resize - no border/9-slice logic, just
        // scaling an already-finished bitmap down (or up) uniformly.
        private static Texture2D ResampleBilinear(Texture2D source, int targetW, int targetH)
        {
            var prevFilter = source.filterMode;
            RenderTexture rt = null;
            var prevActive = RenderTexture.active;
            try
            {
                source.filterMode = FilterMode.Bilinear;
                rt = RenderTexture.GetTemporary(targetW, targetH, 0, RenderTextureFormat.ARGB32);
                rt.filterMode = FilterMode.Bilinear;
                Graphics.Blit(source, rt);

                RenderTexture.active = rt;
                var readable = new Texture2D(targetW, targetH, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, targetW, targetH), 0, 0);
                readable.Apply();
                return readable;
            }
            finally
            {
                source.filterMode = prevFilter;
                RenderTexture.active = prevActive;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
            }
        }
    }
}
