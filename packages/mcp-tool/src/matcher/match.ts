// Orchestrates the matcher pipeline end-to-end for one element-tree.json
// against one catalog: per matchable element, candidates() -> visualSignal()
// + structuralSignal() (per candidate, in parallel) -> gate(), producing a
// MatchResult array (match-result.schema.json).
//
// Not part of the original T2.x task numbering - identified as a genuine
// gap while starting T2.6: score_gate2.py needs something to diff against
// golden-matches.json, and nothing yet assembled candidates.ts,
// visual-signal.ts, structural-signal.ts, and gate.ts into one pass over a
// whole element tree.

import type { CatalogEntry, ElementTree, MatchResult } from "@ui-assembler-slice/contracts";
import { candidates } from "./candidates.js";
import type { FrameMeta } from "./element-crop.js";
import { gate, type ScoredCandidate } from "./gate.js";
import { structuralSignal } from "./structural-signal.js";
import { visualSignal, type VisualSignalOptions } from "./visual-signal.js";

type Element = ElementTree["elements"][number];

export interface MatchElementTreeOptions extends VisualSignalOptions {
  topK?: number;
  onProgress?: (done: number, total: number, elementId: string) => void;
  // figma_node_ids with a real hi-res Combine-group capture available (see
  // reduce-from-selection.ts's fallbackCaptures / cli.ts's
  // element-fallback-captures.json) - a composite element that scores
  // "missing" against the existing catalog gets flagged
  // "fallback_eligible" instead when its figma_node_id is in this set, so
  // Stage 3 review can offer importing the capture as a new catalog entry
  // rather than just reporting a plain miss.
  fallbackCaptureIds?: Set<string>;
}

// Figma nodes reduced to type "text" become TextMeshPro objects
// (NodeBuilder.cs, Week 3), not Image/prefab instances - matching them
// against the sprite/prefab catalog is a category error. Confirmed against
// golden-matches.json: its entries cover every non-text golden leaf element
// and skip exactly the two type: "text" ones (text_title, text_subtitle).
function isMatchable(element: Element): boolean {
  return element.type !== "text";
}

// The signal that a parent's children are a composite (Figma plugin
// "Combine") group rather than an ordinary layout grouping: an explicit
// flag (element-tree.schema.json's `composite`), set by whoever authors the
// tree, not inferred from geometry. Composite layers are NOT guaranteed to
// share an identical rect - button_x's real Figma geometry (frame/base/
// icon) turned out to be 3 different, nested/overlapping rects, which is
// exactly why a composite group is matched as ONE unit against its own
// rect/crop (below) rather than decomposed into its children: an earlier
// design decomposed composite children into N separately-matched leaves
// scored via a shared joint crop, but real data showed even genuinely-
// correct-per-layer cases (button_x's 3 real, separate, already-existing
// catalog sprites) score wrong or too low that way, because each child's
// own crop is contaminated by its overlapping siblings. Matching the WHOLE
// group as one element against its own rect - same as any ordinary element
// - sidesteps that entirely. If nothing in the existing catalog scores well
// enough, `fallbackCaptureIds` (see MatchElementTreeOptions) is how a
// human-captured real image for the group gets a second chance instead of
// a plain miss - see the "fallback_eligible" handling in the main loop.
function isCompositeGroup(element: Element): boolean {
  return element.composite === true && element.children.length > 1;
}

// A composite group's `children` still carry real sub-layer geometry/ids
// (useful provenance - e.g. NodeBuilder.cs could one day use them for
// per-layer debugging), but are never matched individually - only the
// group's own wrapper element is. Ordinary (non-composite) grouping
// containers have no single catalog entry that represents "the whole
// group," so they're never matched directly either - only their children,
// recursively.
function collectWorkItems(elements: Element[]): Element[] {
  const result: Element[] = [];
  for (const element of elements) {
    if (isCompositeGroup(element)) {
      if (isMatchable(element)) result.push(element);
    } else if (element.children.length > 0) {
      result.push(...collectWorkItems(element.children));
    } else if (isMatchable(element)) {
      result.push(element);
    }
  }
  return result;
}

export async function matchElementTree(
  elementTree: ElementTree,
  catalog: CatalogEntry,
  options: MatchElementTreeOptions = {},
): Promise<MatchResult> {
  const frame: FrameMeta = {
    source_frame: elementTree.source_frame,
    canvas_reference: elementTree.canvas_reference,
    canvas_match_mode: elementTree.canvas_match_mode,
    canvas_match_value: elementTree.canvas_match_value,
  };

  const workItems = collectWorkItems(elementTree.elements);
  const total = workItems.length;
  const results: MatchResult = [];
  let done = 0;

  // Sequential across elements, parallel (bounded by topK, ~10) across
  // candidates within one element - keeps peak concurrent work bounded
  // without needing a general-purpose concurrency limiter, which a
  // ~10-15-element slice-scale run doesn't need. Composite groups (see
  // isCompositeGroup) go through this exact same path, matched as one unit
  // against their own rect - no separate branch needed anymore.
  for (const element of workItems) {
    const topCandidates = candidates(element, catalog, options.topK);
    const scoredCandidates: ScoredCandidate[] = await Promise.all(
      topCandidates.map(async (candidate) => ({
        candidate,
        structural: structuralSignal(element, candidate),
        visual: await visualSignal(element, frame, candidate, options),
      })),
    );
    let result = gate(element, scoredCandidates);
    if (
      result.status === "missing" &&
      element.composite === true &&
      options.fallbackCaptureIds?.has(element.figma_node_id)
    ) {
      // No existing catalog entry scored well enough, but a real
      // hi-res capture of this Combine group exists (see
      // MatchElementTreeOptions.fallbackCaptureIds) - flag it for a human
      // to import as a new catalog entry in Stage 3 review instead of
      // reporting a plain, dead-end miss.
      result = { ...result, status: "fallback_eligible" };
    }
    results.push(result);
    done++;
    options.onProgress?.(done, total, element.id);
  }

  return results;
}
