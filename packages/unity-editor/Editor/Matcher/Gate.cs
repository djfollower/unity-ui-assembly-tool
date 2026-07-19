using System;
using System.Collections.Generic;
using System.Linq;
using UiAssemblerSlice.Editor.Assembler;

namespace UiAssemblerSlice.Editor.Matcher
{
    internal readonly struct ScoredCandidate
    {
        public readonly CatalogEntryData Candidate;
        public readonly double Visual;
        public readonly double Structural;

        public ScoredCandidate(CatalogEntryData candidate, double visual, double structural)
        {
            Candidate = candidate;
            Visual = visual;
            Structural = structural;
        }
    }

    // Ports matcher/gate.ts verbatim - two-factor + margin. Combines
    // visual + structural signals: agreement check, margin-over-runner-up
    // check, emits matched/uncertain/missing. Thresholds/weights unchanged -
    // see the original file for the tuning history behind these specific
    // numbers (re-tuned once already, against real thumbnail renders).
    internal static class Gate
    {
        private const double MatchVisualThreshold = 0.35;
        private const double MissingVisualThreshold = 0.24;
        private const double MarginThreshold = 0.05;
        private const double WeightVisual = 0.4;
        private const double WeightStructural = 0.6;
        private const double AspectDistortionTolerance = 0.1;
        private const double ResizeEpsilon = 0.5;

        private static Dictionary<string, object> ComputeResize(RectData target, CatalogEntryData candidate)
        {
            var nativeW = candidate.NativeSize.W;
            var nativeH = candidate.NativeSize.H;
            var widthDiff = Math.Abs(target.W - nativeW);
            var heightDiff = Math.Abs(target.H - nativeH);
            if (widthDiff <= ResizeEpsilon && heightDiff <= ResizeEpsilon) return null;

            var sizeDeltaTo = new Dictionary<string, object> { ["w"] = (double)target.W, ["h"] = (double)target.H };
            var imageType = candidate.ImageType;
            if (imageType == "Sliced" || imageType == "Tiled")
            {
                // The entire point of 9-slicing/tiling is absorbing an
                // arbitrary target size without visible distortion.
                return new Dictionary<string, object> { ["safe"] = true, ["size_delta_to"] = sizeDeltaTo };
            }

            var scaleX = target.W / nativeW;
            var scaleY = target.H / nativeH;
            var maxScale = Math.Max(scaleX, scaleY);
            var aspectDelta = maxScale == 0 ? 0 : Math.Abs(scaleX - scaleY) / maxScale;
            return new Dictionary<string, object> { ["safe"] = aspectDelta <= AspectDistortionTolerance, ["size_delta_to"] = sizeDeltaTo };
        }

        // Min-max normalizes one signal across this element's own candidate
        // set - makes visual (pixel-metric scale) and structural
        // (token-overlap scale) commensurable for combining. Ties (zero
        // range) normalize to a neutral 0.5 for everyone.
        private static List<double> NormalizedScores(List<ScoredCandidate> scoredCandidates, bool useVisual)
        {
            var values = scoredCandidates.Select(c => useVisual ? c.Visual : c.Structural).ToList();
            var min = values.Min();
            var max = values.Max();
            var range = max - min;
            return values.Select(v => range > 0 ? (v - min) / range : 0.5).ToList();
        }

        public static MatchResultEntry Score(ElementData element, List<ScoredCandidate> scoredCandidates)
        {
            if (scoredCandidates.Count == 0)
            {
                return new MatchResultEntry
                {
                    ElementId = element.Id,
                    Status = "missing",
                    MatchedAssetId = null,
                    RawSignals = new Dictionary<string, object>
                    {
                        ["visual"] = 0.0,
                        ["structural"] = 0.0,
                        ["agree"] = false,
                        ["margin"] = 0.0,
                    },
                };
            }

            var normVisual = NormalizedScores(scoredCandidates, true);
            var normStructural = NormalizedScores(scoredCandidates, false);
            var combined = scoredCandidates
                .Select((c, i) => (candidate: c, combinedScore: normVisual[i] * WeightVisual + normStructural[i] * WeightStructural))
                .OrderByDescending(x => x.combinedScore)
                .ToList();

            var best = combined[0];
            var margin = best.combinedScore - (combined.Count > 1 ? combined[1].combinedScore : 0.0);

            // Corroboration, not selection - does at least one of the two
            // signals, taken on its OWN, independently pick the same winner
            // the combined score settled on?
            var bestByVisual = scoredCandidates.Aggregate((a, b) => b.Visual > a.Visual ? b : a);
            var bestByStructural = scoredCandidates.Aggregate((a, b) => b.Structural > a.Structural ? b : a);
            var agree = bestByVisual.Candidate.Id == best.candidate.Candidate.Id
                || bestByStructural.Candidate.Id == best.candidate.Candidate.Id;

            var rawSignals = new Dictionary<string, object>
            {
                ["visual"] = best.candidate.Visual,
                ["structural"] = best.candidate.Structural,
                ["agree"] = agree,
                ["margin"] = margin,
            };

            if (best.candidate.Visual < MissingVisualThreshold)
            {
                return new MatchResultEntry
                {
                    ElementId = element.Id,
                    Status = "missing",
                    MatchedAssetId = null,
                    RawSignals = rawSignals,
                };
            }

            var resize = ComputeResize(element.Rect, best.candidate.Candidate);
            var isConfidentMatch = agree && best.candidate.Visual >= MatchVisualThreshold && margin >= MarginThreshold;

            return new MatchResultEntry
            {
                ElementId = element.Id,
                Status = isConfidentMatch ? "matched" : "uncertain",
                MatchedAssetId = best.candidate.Candidate.Id,
                RawSignals = rawSignals,
                RawResize = resize,
            };
        }
    }
}
