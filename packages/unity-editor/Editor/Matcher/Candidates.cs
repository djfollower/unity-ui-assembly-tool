using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UiAssemblerSlice.Editor.Assembler;

namespace UiAssemblerSlice.Editor.Matcher
{
    // Ports matcher/candidates.ts verbatim - coarse pre-filter by name/
    // description token-overlap (IDF-weighted) before the two-factor gate
    // (StructuralSignal + VisualSignal) scores candidates properly. Keeps
    // that cost to top K per element instead of the full catalog.
    internal static class Candidates
    {
        private const int DefaultTopK = 10;
        // A generous ceiling, not "always the whole catalog" - see
        // candidates.ts's own comment on WEAK_SIGNAL_TOP_K.
        private const int WeakSignalTopK = 100;

        private static readonly Regex GenericNamePattern = new Regex(
            @"^(rectangle|ellipse|frame|group|vector|line|polygon|star|component|instance|boolean)\s*\d*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // True when an element's own naming/tagging gives the IDF retrieval
        // below nothing real to work with - see candidates.ts's own comment
        // for why the fix is to skip narrowing rather than risk excluding
        // the correct candidate before visual-signal ever looks at it.
        public static bool IsWeakSignal(ElementData element)
        {
            var bareId = element.Id.Split(new[] { "__" }, StringSplitOptions.None)[0].Trim();
            var genericName = GenericNamePattern.IsMatch(bareId);
            var genericType = string.IsNullOrEmpty(element.Type) || element.Type == "other";
            return genericName && genericType;
        }

        private static HashSet<string> ElementTokens(ElementData element)
        {
            var parts = new List<string> { element.Id, element.Type, element.VisualDescription };
            if (!string.IsNullOrEmpty(element.TextContent)) parts.Add(element.TextContent);
            return Tokenize.Tokens(string.Join(" ", parts));
        }

        private static HashSet<string> CatalogEntryTokens(CatalogEntryData entry)
        {
            var basename = entry.Path.Split('/').Last();
            var parts = new[] { entry.Id, basename, entry.Role, entry.VisualDescription };
            return Tokenize.Tokens(string.Join(" ", parts));
        }

        // Inverse document frequency over the catalog's own vocabulary,
        // smoothed - see candidates.ts's own comment on why plain token
        // overlap isn't enough (generic prefixes shared by many unrelated
        // entries bury the correct match).
        private static Dictionary<string, double> IdfByToken(List<CatalogEntryData> catalog)
        {
            var documentFrequency = new Dictionary<string, int>();
            foreach (var entry in catalog)
            {
                foreach (var token in CatalogEntryTokens(entry))
                {
                    documentFrequency.TryGetValue(token, out var count);
                    documentFrequency[token] = count + 1;
                }
            }
            var n = catalog.Count;
            var idf = new Dictionary<string, double>();
            foreach (var kvp in documentFrequency)
            {
                idf[kvp.Key] = Math.Log((n + 1.0) / (kvp.Value + 1.0)) + 1;
            }
            return idf;
        }

        private static double Weight(Dictionary<string, double> idf, string token)
        {
            return idf.TryGetValue(token, out var w) ? w : 0;
        }

        private static double WeightedNormSquared(HashSet<string> tokens, Dictionary<string, double> idf)
        {
            double sumSquares = 0;
            foreach (var token in tokens)
            {
                var weight = Weight(idf, token);
                sumSquares += weight * weight;
            }
            return sumSquares;
        }

        // Coarse name/description similarity pre-filter. Scores by
        // IDF-weighted token overlap, normalized by each candidate's own
        // token weight. Does not filter out zero-score entries by count
        // alone, so elements with no real catalog match still get a
        // K-sized candidate set for the gate to reject. Returns the top K,
        // highest first.
        public static List<CatalogEntryData> TopCandidates(ElementData element, List<CatalogEntryData> catalog, int topK = DefaultTopK)
        {
            var idf = IdfByToken(catalog);
            var elementSet = ElementTokens(element);
            var effectiveTopK = IsWeakSignal(element) ? Math.Min(catalog.Count, WeakSignalTopK) : topK;

            return catalog
                .Select(entry =>
                {
                    var entrySet = CatalogEntryTokens(entry);
                    double dotProduct = 0;
                    foreach (var token in elementSet)
                    {
                        if (entrySet.Contains(token))
                        {
                            var weight = Weight(idf, token);
                            dotProduct += weight * weight;
                        }
                    }
                    var norm = Math.Sqrt(WeightedNormSquared(entrySet, idf));
                    var score = norm == 0 ? 0 : dotProduct / norm;
                    return (entry, score);
                })
                .OrderByDescending(x => x.score)
                .Take(effectiveTopK)
                .Select(x => x.entry)
                .ToList();
        }
    }
}
