// T2.2: tiered retrieval, single-feature scope for the slice (tiering across
// features deferred, R11). Coarse pre-filter by name/description similarity
// before the two-factor gate (visual-signal.ts + structural-signal.ts, both
// LLM calls) scores candidates properly - this step is what keeps that cost
// to top K per element instead of the full catalog. Threshold/K are expected
// to move during T2.7's tuning pass.

import type { CatalogEntry, ElementTree } from "@ui-assembler-slice/contracts";
import { tokenize } from "./tokenize.js";

const DEFAULT_TOP_K = 10;

// Figma's own auto-generated default names, tested against the id with any
// reduce-from-selection.ts collision-disambiguation suffix stripped (e.g.
// "Rectangle 70__178-34525" -> "Rectangle 70").
const GENERIC_NAME_PATTERN =
  /^(rectangle|ellipse|frame|group|vector|line|polygon|star|component|instance|boolean)\s*\d*$/i;

// A generous ceiling, not "always the whole catalog" - the catalog is
// expected to grow over successive runs (see fallback-asset-generation in
// project memory), so this shouldn't scale unboundedly. Comfortably above
// today's real catalog size (65) so nothing is actually truncated in
// practice right now - revisit this constant if the catalog approaches it.
const WEAK_SIGNAL_TOP_K = 100;

// True when an element's own naming/tagging gives the IDF retrieval below
// nothing real to work with - checked against the real catalog: role/
// feature tags are uniform across every entry (no filterable category
// signal there), and a generic Figma default name + a generic/absent type
// tag (composite leaf members never get an individual type picker in the
// plugin UI) means token-overlap ranking is close to arbitrary for these.
// Better to skip the narrowing pre-filter and let the small catalog's
// (near-)full set reach the real pixel comparison in visual-signal.ts,
// rather than risk excluding the correct candidate before it's ever
// actually looked at.
export function isWeakSignal(element: ElementTree["elements"][number]): boolean {
  const bareId = element.id.split("__")[0].trim();
  const genericName = GENERIC_NAME_PATTERN.test(bareId);
  const genericType = !element.type || element.type === "other";
  return genericName && genericType;
}

function elementTokens(element: ElementTree["elements"][number]): Set<string> {
  const parts = [element.id, element.type, element.visual_description];
  if (element.text_content) parts.push(element.text_content);
  return tokenize(parts.join(" "));
}

function catalogEntryTokens(entry: CatalogEntry[number]): Set<string> {
  const basename = entry.path.split("/").pop() ?? entry.path;
  const parts = [entry.id, basename, entry.role, entry.visual_description];
  return tokenize(parts.join(" "));
}

// Inverse document frequency over the catalog's own vocabulary, smoothed.
// Plain token overlap (Jaccard) ranks candidates by raw shared-word count,
// which buries the correct match under generic prefixes shared by many
// unrelated entries in this catalog (e.g. "icon_glow" vs. "ui_img_glow" loses
// to "icon_heart" on the bare word "icon" - observed against the real
// catalog/fixtures). Weighting by rarity fixes that: a shared "glow" (unique
// to one entry) should outweigh a shared "icon" (in ~20 entries).
function idfByToken(catalog: CatalogEntry): Map<string, number> {
  const documentFrequency = new Map<string, number>();
  for (const entry of catalog) {
    for (const token of catalogEntryTokens(entry)) {
      documentFrequency.set(token, (documentFrequency.get(token) ?? 0) + 1);
    }
  }
  const n = catalog.length;
  const idf = new Map<string, number>();
  for (const [token, count] of documentFrequency) {
    idf.set(token, Math.log((n + 1) / (count + 1)) + 1);
  }
  return idf;
}

function weightedNormSquared(tokens: Set<string>, idf: Map<string, number>): number {
  let sumSquares = 0;
  for (const token of tokens) {
    const weight = idf.get(token) ?? 0;
    sumSquares += weight * weight;
  }
  return sumSquares;
}

// Coarse name/description similarity pre-filter (R11: single feature, no
// cross-feature tiering for the slice - the whole catalog passed in is
// already scoped to one feature by load-catalog.ts). Scores by IDF-weighted
// token overlap, normalized by each candidate's own token weight (so a
// terse id like "button_x" isn't penalized against a longer one). Does not
// filter out zero-score entries by count alone, so elements with no real
// catalog match (e.g. the missing-recall fixture cases) still get a
// K-sized candidate set for the gate to reject. Returns the top K, highest
// first.
export function candidates(
  element: ElementTree["elements"][number],
  catalog: CatalogEntry,
  topK: number = DEFAULT_TOP_K,
): CatalogEntry {
  const idf = idfByToken(catalog);
  const elementSet = elementTokens(element);
  const effectiveTopK = isWeakSignal(element) ? Math.min(catalog.length, WEAK_SIGNAL_TOP_K) : topK;
  return catalog
    .map((entry) => {
      const entrySet = catalogEntryTokens(entry);
      let dotProduct = 0;
      for (const token of elementSet) {
        if (entrySet.has(token)) {
          const weight = idf.get(token) ?? 0;
          dotProduct += weight * weight;
        }
      }
      const norm = Math.sqrt(weightedNormSquared(entrySet, idf));
      const score = norm === 0 ? 0 : dotProduct / norm;
      return { entry, score };
    })
    .sort((a, b) => b.score - a.score)
    .slice(0, effectiveTopK)
    .map(({ entry }) => entry);
}
