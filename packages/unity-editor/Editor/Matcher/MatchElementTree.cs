using System.Collections.Generic;
using System.Linq;
using UiAssemblerSlice.Editor.Assembler;

namespace UiAssemblerSlice.Editor.Matcher
{
    // Ports matcher/match.ts's matchElementTree orchestration - runs
    // Candidates -> StructuralSignal + VisualSignal -> Gate over every
    // matchable element in an ElementTreeData, in-process. Replaces the old
    // `npm run cli -- match` subprocess step (see ReviewWindow.RunPipeline) -
    // no JSON round-trip between "match" and "review" anymore.
    internal static class MatchElementTree
    {
        private const int DefaultTopK = 10;

        // Figma nodes reduced to type "text" become TextMeshPro objects
        // (NodeBuilder.cs), not Image/prefab instances - matching them
        // against the sprite/prefab catalog is a category error.
        private static bool IsMatchable(ElementData element) => element.Type != "text";

        // The signal that a parent's children are a composite (Figma plugin
        // "Combine") group rather than an ordinary layout grouping - matched
        // as ONE unit against its own rect, not decomposed into children
        // (see match.ts's own comment on why: overlapping/nested composite
        // children score wrong or too low when matched individually, even
        // when each has a correct catalog counterpart).
        private static bool IsCompositeGroup(ElementData element) => element.Composite && element.Children.Count > 1;

        private static void CollectWorkItems(List<ElementData> elements, List<ElementData> into)
        {
            foreach (var element in elements)
            {
                if (IsCompositeGroup(element))
                {
                    if (IsMatchable(element)) into.Add(element);
                }
                else if (element.Children.Count > 0)
                {
                    CollectWorkItems(element.Children, into);
                }
                else if (IsMatchable(element))
                {
                    into.Add(element);
                }
            }
        }

        // elementFallbackCaptures doubles as match.ts's fallbackCaptureIds
        // (a composite element scoring "missing" gets promoted to
        // "fallback_eligible" instead when its figma_node_id has a real
        // hi-res capture available) - the same dictionary VisualSignal
        // already reads ground truth from, so no separate id set is needed.
        public static List<MatchResultEntry> Run(
            ElementTreeData elementTree,
            List<CatalogEntryData> catalog,
            IReadOnlyDictionary<string, string> elementThumbnails,
            IReadOnlyDictionary<string, string> elementFallbackCaptures,
            int topK = DefaultTopK)
        {
            var workItems = new List<ElementData>();
            CollectWorkItems(elementTree.Elements, workItems);

            var results = new List<MatchResultEntry>();
            foreach (var element in workItems)
            {
                // Warn once per element here, not once per candidate inside
                // VisualSignal.Score - a missing ground-truth image is a
                // normal, expected gap (see VisualSignal.ResolveGroundTruth's
                // comment), not a reason to abort the whole match run, but
                // still worth surfacing so it's not silently mistaken for a
                // real "no visual match" verdict.
                if (!VisualSignal.HasGroundTruth(element, elementThumbnails, elementFallbackCaptures))
                {
                    UnityEngine.Debug.LogWarning(
                        $"MatchElementTree: no ground-truth image for element '{element.Id}' (figma_node_id '{element.FigmaNodeId}') - " +
                        "scoring its visual signal as 0. If this is a composite (Combine) element, its hi-res capture " +
                        "may not have been taken yet - re-run the Figma plugin to retry it, or re-export.");
                }

                var topCandidates = Candidates.TopCandidates(element, catalog, topK);
                var scored = topCandidates
                    .Select(candidate => new ScoredCandidate(
                        candidate,
                        VisualSignal.Score(element, candidate, elementThumbnails, elementFallbackCaptures),
                        StructuralSignal.Score(element, candidate)))
                    .ToList();

                var result = Gate.Score(element, scored);
                if (result.Status == "missing"
                    && element.Composite
                    && !string.IsNullOrEmpty(element.FigmaNodeId)
                    && elementFallbackCaptures.ContainsKey(element.FigmaNodeId))
                {
                    result.Status = "fallback_eligible";
                }
                results.Add(result);
            }

            return results;
        }
    }
}
