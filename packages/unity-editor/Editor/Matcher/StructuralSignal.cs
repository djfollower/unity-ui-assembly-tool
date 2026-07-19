using System.Collections.Generic;
using System.Linq;
using UiAssemblerSlice.Editor.Assembler;

namespace UiAssemblerSlice.Editor.Matcher
{
    // Ports matcher/structural-signal.ts verbatim - layer-name-to-asset-name
    // similarity, component-instance-to-prefab match, description
    // similarity, combined into a single structural score (the counterpart
    // to VisualSignal's pixel comparison). Weights unchanged - see the
    // original file for the tuning history behind these specific numbers
    // (WEIGHT_INSTANCE in particular is deliberately low, not 1/3).
    internal static class StructuralSignal
    {
        private const double WeightName = 0.4;
        private const double WeightInstance = 0.05;
        private const double WeightDescription = 0.55;

        private static HashSet<string> ElementNameTokens(ElementData element)
        {
            return Tokenize.Tokens($"{element.Id} {element.Type}");
        }

        private static HashSet<string> CandidateIdentityTokens(CatalogEntryData entry)
        {
            var basename = entry.Path.Split('/').Last();
            return Tokenize.Tokens($"{entry.Id} {basename} {entry.Role}");
        }

        private static double NameSimilarity(ElementData element, CatalogEntryData candidate)
        {
            return Tokenize.JaccardSimilarity(ElementNameTokens(element), CandidateIdentityTokens(candidate));
        }

        // Figma's is_component_instance is a template hint, not a hard rule
        // - rewards the prefab+instance combination without punishing the
        // equally-valid instance+sprite one; a non-instance element matching
        // a prefab gets a mild penalty instead.
        private static double InstanceScore(ElementData element, CatalogEntryData candidate)
        {
            if (candidate.Type != "prefab") return 0.5;
            return element.IsComponentInstance ? 1 : 0.3;
        }

        // Element's own visual_description (+ any text_content) against the
        // candidate's FULL textual identity (id/path/role AND description)
        // - see structural-signal.ts's own comment on why description-only
        // isn't enough while catalog descriptions are still sparse.
        private static double DescriptionSimilarity(ElementData element, CatalogEntryData candidate)
        {
            var elementText = !string.IsNullOrEmpty(element.TextContent)
                ? $"{element.VisualDescription} {element.TextContent}"
                : element.VisualDescription;
            var candidateText = $"{candidate.Id} {candidate.Path} {candidate.Role} {candidate.VisualDescription}";
            return Tokenize.JaccardSimilarity(Tokenize.Tokens(elementText), Tokenize.Tokens(candidateText));
        }

        public static double Score(ElementData element, CatalogEntryData candidate)
        {
            var nameScore = NameSimilarity(element, candidate);
            var instScore = InstanceScore(element, candidate);
            var descScore = DescriptionSimilarity(element, candidate);
            return nameScore * WeightName + instScore * WeightInstance + descScore * WeightDescription;
        }
    }
}
