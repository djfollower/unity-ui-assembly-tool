using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UiAssemblerSlice.Editor.Assembler;
using UiAssemblerSlice.Editor.Catalog;

namespace UiAssemblerSlice.Editor.Matcher
{
    // Ports matcher/visual-signal.ts's render-aware comparison (SSIM +
    // color histogram, no LLM call - see the original file's own history
    // comment on why). Two structural differences from the original,
    // per the migration plan:
    //
    // 1. Ground truth source (the crop-source fix): the original cropped
    //    fixtures/frame-export.png, a fixture-only cached frame screenshot
    //    that real projects never produce (HANDOFF.md). Real per-element
    //    ground-truth images already exist for every real run - captured by
    //    the Figma plugin into element-thumbnails.json /
    //    element-fallback-captures.json - just unused by the old visual
    //    signal until now. ResolveGroundTruth below reads those directly;
    //    element-crop.ts's frame-export.png inverse-scale cropping has no
    //    equivalent here, by design.
    //
    // 2. Candidate rendering: the original (render-candidate.ts) detected
    //    content bounds in a pre-baked, padded-to-square thumbnail PNG and
    //    hand-rolled a 9-slice compositor in pure JS, because Node could
    //    never see the live Unity asset. RenderedThumbnail.RenderAtExactSize
    //    renders the live asset directly via Unity's own border rendering -
    //    see that method's own comment.
    //
    // Known divergence from the original, accepted rather than chased:
    // sharp's default resize kernel is Lanczos3; ResampleBilinear here (and
    // in RenderedThumbnail generally) is bilinear. Both are reasonable
    // resampling filters; this can shift SSIM by a small amount on
    // borderline candidates. If Gate 2 thresholds drift after this
    // migration, this is the first place to look - not the SSIM port
    // itself (see Ssim.cs, verified bit-for-bit against ssim.js).
    internal static class VisualSignal
    {
        public const int ComparisonSize = 64;
        private const int HistogramBinsPerChannel = 8;
        private const double WeightStructural = 0.6;
        private const double WeightColor = 0.4;
        private static readonly Color32 FlattenBackground = new Color32(128, 128, 128, 255);

        // Returns null (not a throw) when no capture/thumbnail is on record -
        // a synthetic composite sub-element (figma_node_id "combine:<groupId>",
        // never a real Figma node) has NO possible ground truth unless its
        // group's hi-res Combine capture was actually taken; that capture is
        // best-effort (async export, in-memory only for the plugin session -
        // see figma-plugin/code.js's own comment on why it doesn't survive a
        // plugin reload) and its absence is a normal, expected gap, not a
        // data-integrity bug - Score() below treats it as "no visual
        // evidence" rather than aborting the whole match run over one element.
        private static Texture2D ResolveGroundTruth(
            ElementData element,
            IReadOnlyDictionary<string, string> elementThumbnails,
            IReadOnlyDictionary<string, string> elementFallbackCaptures)
        {
            if (string.IsNullOrEmpty(element.FigmaNodeId))
            {
                throw new InvalidOperationException($"VisualSignal: element '{element.Id}' has no figma_node_id to resolve a ground-truth image from");
            }
            // Prefer the full-resolution Combine capture when one exists
            // (composite elements) - a better ground truth than the 64px
            // plugin thumbnail every element also carries.
            if (elementFallbackCaptures.TryGetValue(element.FigmaNodeId, out var hiRes))
            {
                return DecodeDataUri(hiRes);
            }
            if (elementThumbnails.TryGetValue(element.FigmaNodeId, out var thumb))
            {
                return DecodeDataUri(thumb);
            }
            return null;
        }

        // Key-only check (no decode) so MatchElementTree can warn once per
        // element instead of Score() below warning once per candidate.
        public static bool HasGroundTruth(
            ElementData element,
            IReadOnlyDictionary<string, string> elementThumbnails,
            IReadOnlyDictionary<string, string> elementFallbackCaptures)
        {
            if (string.IsNullOrEmpty(element.FigmaNodeId)) return false;
            return elementFallbackCaptures.ContainsKey(element.FigmaNodeId) || elementThumbnails.ContainsKey(element.FigmaNodeId);
        }

        // Same data-URI decode convention as ReviewWindow.DecodeDataUriToTexture.
        private static Texture2D DecodeDataUri(string dataUri)
        {
            var comma = dataUri.IndexOf(',');
            var base64 = comma >= 0 ? dataUri.Substring(comma + 1) : dataUri;
            var bytes = Convert.FromBase64String(base64);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            if (!tex.LoadImage(bytes, markNonReadable: false))
            {
                UnityEngine.Object.DestroyImmediate(tex);
                throw new InvalidOperationException("VisualSignal: failed to decode ground-truth data URI as PNG");
            }
            return tex;
        }

        // Flattens alpha onto a neutral gray background (matches
        // visual-signal.ts's toComparableImage - keeps a candidate's
        // transparent padding from biasing the color histogram against
        // otherwise-correct candidates), THEN resizes to
        // ComparisonSize x ComparisonSize - same order as the original.
        private static Color32[] ToComparable(Texture2D source)
        {
            var srcPixels = source.GetPixels32();
            var flattened = new Color32[srcPixels.Length];
            for (var i = 0; i < srcPixels.Length; i++)
            {
                var p = srcPixels[i];
                var a = p.a / 255f;
                flattened[i] = new Color32(
                    (byte)Mathf.RoundToInt(p.r * a + FlattenBackground.r * (1 - a)),
                    (byte)Mathf.RoundToInt(p.g * a + FlattenBackground.g * (1 - a)),
                    (byte)Mathf.RoundToInt(p.b * a + FlattenBackground.b * (1 - a)),
                    255);
            }

            var flattenedTex = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                flattenedTex.SetPixels32(flattened);
                flattenedTex.Apply();
                var resized = RenderedThumbnail.ResampleBilinear(flattenedTex, ComparisonSize, ComparisonSize);
                try
                {
                    return resized.GetPixels32();
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(resized);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(flattenedTex);
            }
        }

        // Per-channel (R,G,B) normalized histogram - matches
        // visual-signal.ts::colorHistogram exactly.
        private static double[] ColorHistogram(Color32[] pixels)
        {
            var histogram = new double[HistogramBinsPerChannel * 3];
            foreach (var p in pixels)
            {
                BinInto(histogram, 0, p.r);
                BinInto(histogram, 1, p.g);
                BinInto(histogram, 2, p.b);
            }
            var pixelCount = pixels.Length;
            for (var i = 0; i < histogram.Length; i++) histogram[i] /= pixelCount;
            return histogram;
        }

        private static void BinInto(double[] histogram, int channel, byte value)
        {
            var bin = Mathf.Min(HistogramBinsPerChannel - 1, Mathf.FloorToInt((value / 256f) * HistogramBinsPerChannel));
            histogram[channel * HistogramBinsPerChannel + bin] += 1;
        }

        // Histogram intersection, normalized to 0-1 - matches
        // visual-signal.ts::histogramSimilarity exactly.
        private static double HistogramSimilarity(double[] a, double[] b)
        {
            double intersection = 0;
            for (var i = 0; i < a.Length; i++) intersection += Math.Min(a[i], b[i]);
            return intersection / 3;
        }

        // matcher/visual-signal.ts::compareImages - SSIM + color-histogram
        // comparison of two already-rendered images.
        public static double CompareImages(Texture2D imageA, Texture2D imageB)
        {
            var pixelsA = ToComparable(imageA);
            var pixelsB = ToComparable(imageB);

            var structuralScore = Ssim.Compare(pixelsA, pixelsB, ComparisonSize, ComparisonSize);
            var colorScore = HistogramSimilarity(ColorHistogram(pixelsA), ColorHistogram(pixelsB));

            var combined = structuralScore * WeightStructural + colorScore * WeightColor;
            return Math.Min(1, Math.Max(0, combined));
        }

        public static double Score(
            ElementData element,
            CatalogEntryData candidate,
            IReadOnlyDictionary<string, string> elementThumbnails,
            IReadOnlyDictionary<string, string> elementFallbackCaptures)
        {
            var groundTruth = ResolveGroundTruth(element, elementThumbnails, elementFallbackCaptures);
            if (groundTruth == null)
            {
                // No possible ground truth for this element (see
                // ResolveGroundTruth's own comment) - 0 is a meaningful "no
                // visual evidence" score, not a placeholder: Gate's
                // MISSING_VISUAL_THRESHOLD already exists to route exactly
                // this case to "missing" (or "fallback_eligible" if a
                // Combine capture becomes available later) instead of a
                // false-confident match. MatchElementTree logs a warning
                // once per element - not repeated here per candidate.
                return 0.0;
            }
            try
            {
                var targetW = Mathf.Max(1, Mathf.RoundToInt(element.Rect.W));
                var targetH = Mathf.Max(1, Mathf.RoundToInt(element.Rect.H));
                var metadata = new RenderMetadata(
                    candidate.ImageType, candidate.Border, candidate.Ppu, candidate.PpuMultiplier,
                    candidate.NativeSize.W, candidate.NativeSize.H, candidate.TintHex);
                var asset = new DiscoveredAsset(candidate.Path, AssetDatabase.AssetPathToGUID(candidate.Path), candidate.Type, Array.Empty<string>());
                var candidateRender = RenderedThumbnail.RenderAtExactSize(asset, metadata, targetW, targetH);
                try
                {
                    return CompareImages(groundTruth, candidateRender);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(candidateRender);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(groundTruth);
            }
        }
    }
}
