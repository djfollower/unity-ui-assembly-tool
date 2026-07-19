// Shared token-similarity primitives for the matcher. candidates.ts's coarse
// retrieval and structural-signal.ts's per-candidate name/description signal
// both need the same "split identifiers into comparable words" step.

// Splits snake_case, kebab-case, path separators, and camelCase/PascalCase
// into lowercase tokens, e.g. "UIElements__button_green" -> {uielements,
// button, green}.
export function tokenize(text: string): Set<string> {
  const spaced = text
    .replace(/[_\-./]+/g, " ")
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2");
  return new Set(
    spaced
      .toLowerCase()
      .split(/\s+/)
      .filter((token) => token.length > 0),
  );
}

export function jaccardSimilarity(a: Set<string>, b: Set<string>): number {
  if (a.size === 0 || b.size === 0) return 0;
  let intersection = 0;
  for (const token of a) {
    if (b.has(token)) intersection++;
  }
  const union = a.size + b.size - intersection;
  return union === 0 ? 0 : intersection / union;
}
