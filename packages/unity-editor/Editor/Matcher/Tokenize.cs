using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace UiAssemblerSlice.Editor.Matcher
{
    // Ports matcher/tokenize.ts verbatim - shared token-similarity
    // primitives for Candidates' coarse retrieval and StructuralSignal's
    // per-candidate name/description signal.
    internal static class Tokenize
    {
        private static readonly Regex SeparatorPattern = new Regex(@"[_\-./]+", RegexOptions.Compiled);
        private static readonly Regex CamelBoundaryPattern = new Regex(@"([a-z0-9])([A-Z])", RegexOptions.Compiled);
        private static readonly char[] WhitespaceChars = { ' ', '\t', '\n', '\r' };

        // Splits snake_case, kebab-case, path separators, and
        // camelCase/PascalCase into lowercase tokens, e.g.
        // "UIElements__button_green" -> {uielements, button, green}.
        public static HashSet<string> Tokens(string text)
        {
            var spaced = SeparatorPattern.Replace(text ?? "", " ");
            spaced = CamelBoundaryPattern.Replace(spaced, "$1 $2");
            return new HashSet<string>(
                spaced.ToLowerInvariant()
                    .Split(WhitespaceChars, System.StringSplitOptions.RemoveEmptyEntries));
        }

        public static double JaccardSimilarity(HashSet<string> a, HashSet<string> b)
        {
            if (a.Count == 0 || b.Count == 0) return 0;
            var intersection = a.Count(b.Contains);
            var union = a.Count + b.Count - intersection;
            return union == 0 ? 0 : (double)intersection / union;
        }
    }
}
