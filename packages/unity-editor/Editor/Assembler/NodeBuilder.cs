using System.Collections.Generic;
using TMPro;
using UiAssemblerSlice.Editor.Catalog;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace UiAssemblerSlice.Editor.Assembler
{
    /// T3.2: walks confirmed match-result.json + element-tree.json, instantiates
    /// Image/TMP/prefab per element at the absolute reference-space rect (no
    /// anchoring intelligence - out of scope). Applies the R14 auto-resize
    /// (sizeDelta + multiplier) where the match result marked it safe.
    ///
    /// Extended beyond the original stub signature: matched_asset_id only
    /// references a catalog id, so resolving it to a real Unity asset needs
    /// catalog.json too (RunAssemble.cs passes all three already-parsed
    /// inputs in, rather than re-reading element-tree.json a second time).
    public static class NodeBuilder
    {
        public static GameObject BuildElement(
            Transform parent,
            ElementTreeData elementTree,
            List<MatchResultEntry> matchResults,
            List<CatalogEntryData> catalog)
        {
            var matchByElementId = new Dictionary<string, MatchResultEntry>();
            foreach (var match in matchResults) matchByElementId[match.ElementId] = match;

            var catalogById = new Dictionary<string, CatalogEntryData>();
            foreach (var entry in catalog) catalogById[entry.Id] = entry;

            // Depth-first, preserving element-tree.json's own array order:
            // that order is already established (see HANDOFF.md's
            // composite-crop-sharing notes) to be back-to-front, so
            // appending each built node as the next sibling under `parent`
            // makes Unity's sibling-index draw order come out correct with
            // no extra z-ordering logic needed.
            foreach (var element in elementTree.Elements)
            {
                // (0, 0): the canvas root's own top-left is the coordinate
                // origin element-tree.json's rects are already absolute
                // against - see BuildRecursive's originX/originY comment.
                BuildRecursive(element, parent, 0f, 0f, matchByElementId, catalogById);
            }

            return parent.gameObject;
        }

        private static void BuildRecursive(
            ElementData element,
            Transform parent,
            float originX,
            float originY,
            Dictionary<string, MatchResultEntry> matchByElementId,
            Dictionary<string, CatalogEntryData> catalogById)
        {
            // element.Rect is always absolute canvas-space (normalize.ts's
            // convention). originX/originY is the absolute position of
            // `parent`'s own top-left corner - (0,0) at the top of the
            // recursion (canvas root), or a container's own rect.X/Y once
            // we've descended into one (see the Container branch below).
            // Subtracting it converts element.Rect into `parent`-relative
            // coordinates, which is what PositionRect's anchoredPosition
            // math actually needs. When nothing is ever nested under a real
            // container (today's only path, still true for every non-
            // container element), originX/Y stays (0,0) the whole way down
            // and localRect is byte-for-byte identical to element.Rect -
            // this refactor changes nothing for that path.
            var localRect = new RectData(
                element.Rect.X - originX,
                element.Rect.Y - originY,
                element.Rect.W,
                element.Rect.H);

            if (element.Children.Count > 0)
            {
                if (element.Container)
                {
                    // Unlike a pure grouping container (below), this
                    // element IS itself instantiated - a real empty
                    // GameObject its children nest under for real, e.g. so
                    // the whole thing can be scaled/tweened as one unit
                    // post-assembly. Center pivot (PositionContainerRect,
                    // not PositionRect) so that animation happens around
                    // the visual center, not the top-left corner - scoped
                    // to container GameObjects only, every leaf element
                    // below keeps the existing top-left convention
                    // untouched.
                    var containerGo = new GameObject(element.Id, typeof(RectTransform));
                    containerGo.transform.SetParent(parent, false);
                    PositionContainerRect(containerGo.GetComponent<RectTransform>(), localRect);

                    foreach (var child in element.Children)
                    {
                        BuildRecursive(child, containerGo.transform, element.Rect.X, element.Rect.Y, matchByElementId, catalogById);
                    }
                    return;
                }

                // A pure grouping container (same convention matcher/
                // match.ts already uses) - there's no single catalog entry
                // for "the whole composite," so it's never instantiated
                // itself, only recursed into. Flattens to direct siblings
                // under `parent`, same origin - today's only behavior when
                // Container isn't set.
                foreach (var child in element.Children)
                {
                    BuildRecursive(child, parent, originX, originY, matchByElementId, catalogById);
                }
                return;
            }

            if (element.Type == "text")
            {
                BuildText(element, parent, localRect);
                return;
            }

            if (!matchByElementId.TryGetValue(element.Id, out var match))
            {
                Debug.LogWarning($"NodeBuilder: skipping '{element.Id}' - no match-result entry");
                return;
            }

            if (match.Status != "matched" || match.MatchedAssetId == null)
            {
                Debug.LogWarning($"NodeBuilder: skipping '{element.Id}' - status is \"{match.Status}\", not a confirmed match");
                return;
            }

            if (!catalogById.TryGetValue(match.MatchedAssetId, out var entry))
            {
                Debug.LogWarning($"NodeBuilder: skipping '{element.Id}' - matched_asset_id '{match.MatchedAssetId}' not found in catalog");
                return;
            }

            // BuildFromSprite/ResizeMainImageToFill only ever read
            // element.Rect's W/H (Sliced pre-composite target size), never
            // X/Y, so they're unaffected by the absolute-vs-local
            // distinction and keep using element.Rect directly.
            var go = entry.Type == "prefab" ? BuildFromPrefab(entry) : BuildFromSprite(entry, element.Rect);
            go.name = element.Id;
            go.transform.SetParent(parent, false);
            PositionRect(go.GetComponent<RectTransform>(), localRect);

            if (entry.Type == "prefab")
            {
                ResizeMainImageToFill(go, entry, element.Rect);
            }
        }

        // internal, not private: SmokeTest.CaptureCatalogEntryRender reuses
        // this exact method for real-render validation, rather than
        // re-implementing (and risking silently diverging from) the same
        // Image setup a second time.
        //
        // Takes the element's target rect directly (not just resized after
        // the fact by PositionRect) because Sliced entries need it to
        // pre-composite at the right pixel size - see RenderSlicedToTexture.
        internal static GameObject BuildFromSprite(CatalogEntryData entry, RectData targetRect)
        {
            var go = new GameObject(entry.Id, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var image = go.GetComponent<Image>();

            if (entry.ImageType == "Sliced")
            {
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(entry.Path);
                var composited = RenderSlicedToTexture(
                    texture, entry.Border, Mathf.RoundToInt(targetRect.W), Mathf.RoundToInt(targetRect.H));
                image.sprite = Sprite.Create(composited, new Rect(0, 0, composited.width, composited.height), new Vector2(0.5f, 0.5f));
                image.type = Image.Type.Simple;
            }
            else
            {
                image.sprite = AssetDatabase.LoadAssetAtPath<Sprite>(entry.Path);
                image.type = (Image.Type)System.Enum.Parse(typeof(Image.Type), entry.ImageType);
            }

            if (entry.TintHex != null && ColorUtility.TryParseHtmlString(entry.TintHex, out var color))
            {
                image.color = color;
            }
            return go;
        }

        internal static GameObject BuildFromPrefab(CatalogEntryData entry)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(entry.Path);
            // Its own Image/border/tint are already baked in - only
            // positioning/resizing the RectTransform is this method's job
            // (see ResizeMainImageToFill for the part that isn't just the
            // root's own RectTransform).
            return (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        }

        // A prefab's root RectTransform (resized by PositionRect above) is
        // not necessarily where the actual visible image lives -
        // UIElements__ButtonFrame, for example, has an outer root that's a
        // decorative wrapper, with the real button Image on an independently
        // center-anchored, fixed-size CHILD ("ButtonContinue") several
        // levels in - the exact same GameObject
        // RenderMetadataProbe.ResolveMainImage already picked when this
        // entry's render metadata was probed into catalog.json (reused here
        // rather than re-deriving "the main image" a second, possibly
        // inconsistent way). Found by comparing an assembled prefab against
        // its Figma mockup: the inner image was staying frozen at its native
        // size because only the root's RectTransform was being resized.
        // Stretching it to fill its parent keeps it tracking whatever size
        // the root ends up at, with no relative-position math needed (same
        // "no anchoring intelligence" scope as PositionRect).
        //
        // If the main image is Sliced, its sprite/type get replaced with a
        // pre-composited Simple sprite the same way BuildFromSprite does -
        // see RenderSlicedToTexture's comment for why UI.Image's own
        // built-in Sliced rendering can't be trusted here. `entry`'s
        // Border/ImageType already describe THIS specific child (not the
        // root) - RenderMetadataProbe.ResolveMainImage is exactly what T1.5
        // used to probe them into catalog.json in the first place, so
        // they're already correct without re-deriving anything.
        internal static void ResizeMainImageToFill(GameObject root, CatalogEntryData entry, RectData targetRect)
        {
            var mainImage = RenderMetadataProbe.ResolveMainImage(root);
            if (mainImage == null || mainImage.gameObject == root)
            {
                return;
            }

            var rt = mainImage.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            if (entry.ImageType == "Sliced" && mainImage.sprite != null)
            {
                var spritePath = AssetDatabase.GetAssetPath(mainImage.sprite);
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(spritePath);
                var composited = RenderSlicedToTexture(
                    texture, entry.Border, Mathf.RoundToInt(targetRect.W), Mathf.RoundToInt(targetRect.H));
                // mainImage.color already carries whatever tint this prefab
                // variant authored (e.g. ButtonFrameTint's inner child) -
                // left untouched.
                mainImage.sprite = Sprite.Create(composited, new Rect(0, 0, composited.width, composited.height), new Vector2(0.5f, 0.5f));
                mainImage.type = Image.Type.Simple;
            }
        }

        // Pre-composites a Sliced sprite at an arbitrary target size via raw
        // Graphics.DrawTexture against the loose source texture (literal
        // texture-pixel border ints - same convention RenderedThumbnail.cs/
        // render-candidate.ts already use), instead of trusting
        // UnityEngine.UI.Image's own built-in Sliced rendering.
        //
        // Real bug, found by actually rendering and looking (not by
        // review): UI.Image's built-in Sliced path renders these specific
        // sprites (Tight mesh type + packed into a compressed SpriteAtlasV2
        // atlas - confirmed via a real diagnostic dump, not assumed) as a
        // flat, completely unrounded, undetailed rectangle once resized
        // notably away from native size - verified across multiple target
        // sizes including the sprite's own native size (so it's not a
        // resize/9-slice-math bug at all, something about UI.Image's mesh
        // generation for THIS sprite/atlas/mesh-type combination is simply
        // broken in this environment). Graphics.DrawTexture against the
        // exact same texture + exact same border, at the exact same target
        // size, renders correctly (SmokeTest.CaptureRawTextureDrawTexture
        // confirmed this side by side) - so this pre-composites once via
        // that proven-correct path and displays the result as a plain
        // Simple sprite, sidestepping UI.Image's Sliced code path entirely.
        private static Texture2D RenderSlicedToTexture(Texture2D source, float[] border, int targetW, int targetH)
        {
            targetW = Mathf.Max(1, targetW);
            targetH = Mathf.Max(1, targetH);

            var rt = RenderTexture.GetTemporary(targetW, targetH, 0, RenderTextureFormat.ARGB32);
            var prevActive = RenderTexture.active;
            try
            {
                RenderTexture.active = rt;
                GL.Clear(true, true, new Color(0, 0, 0, 0));
                GL.PushMatrix();
                GL.LoadPixelMatrix(0, targetW, targetH, 0);
                // border order is [left, bottom, right, top]; DrawTexture
                // wants (leftBorder, rightBorder, topBorder, bottomBorder).
                Graphics.DrawTexture(new Rect(0, 0, targetW, targetH), source, new Rect(0, 0, 1, 1),
                    (int)border[0], (int)border[2], (int)border[3], (int)border[1], Color.white);
                GL.PopMatrix();

                var readable = new Texture2D(targetW, targetH, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, targetW, targetH), 0, 0);
                readable.Apply();
                return readable;
            }
            finally
            {
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        private static void BuildText(ElementData element, Transform parent, RectData rect)
        {
            var go = new GameObject(element.Id, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = element.TextContent ?? "";
            tmp.alignment = TextAlignmentOptions.Center;
            // element-tree.json carries no structured font/color/size data
            // (visual_description is prose only) - text-fit is an
            // explicitly deferred refinement per the implementation plan's
            // contracts section, so autosizing within the element's own
            // rect is the honest best-effort default, not a real font match.
            tmp.enableAutoSizing = true;
            tmp.fontSizeMin = 1;
            tmp.fontSizeMax = 200;
            PositionRect(go.GetComponent<RectTransform>(), rect);
        }

        // element-tree.json's rects are absolute, y-down from the frame's
        // top-left (see normalize.ts's own comment on this convention) -
        // top-left anchor/pivot makes anchoredPosition a direct copy of
        // (x, -y) with no further math, matching T3.2's "absolute
        // reference-space rect, no anchoring intelligence" scope. Callers
        // pass an already-`parent`-relative rect (BuildRecursive's
        // localRect) - this function does no origin math of its own.
        internal static void PositionRect(RectTransform rt, RectData rect)
        {
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(rect.X, -rect.Y);
            rt.sizeDelta = new Vector2(rect.W, rect.H);
        }

        // Same anchor point (parent's top-left) as PositionRect, so it
        // still needs no knowledge of the parent's own size, but a CENTER
        // pivot instead - only used for container GameObjects (see
        // BuildRecursive), so that animating one (scale/rotate) happens
        // around its visual center rather than its top-left corner. Every
        // leaf element keeps using PositionRect, untouched by this.
        internal static void PositionContainerRect(RectTransform rt, RectData rect)
        {
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(rect.X + rect.W / 2f, -(rect.Y + rect.H / 2f));
            rt.sizeDelta = new Vector2(rect.W, rect.H);
        }
    }
}
