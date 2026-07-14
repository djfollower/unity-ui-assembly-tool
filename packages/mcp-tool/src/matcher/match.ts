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
import { compositeVisualSignals } from "./composite-visual-signal.js";
import type { FrameMeta } from "./element-crop.js";
import { gate, type ScoredCandidate } from "./gate.js";
import { structuralSignal } from "./structural-signal.js";
import { visualSignal, type VisualSignalOptions } from "./visual-signal.js";

type Element = ElementTree["elements"][number];

export interface MatchElementTreeOptions extends VisualSignalOptions {
  topK?: number;
  onProgress?: (done: number, total: number, elementId: string) => void;
}

// Figma nodes reduced to type "text" become TextMeshPro objects
// (NodeBuilder.cs, Week 3), not Image/prefab instances - matching them
// against the sprite/prefab catalog is a category error. Confirmed against
// golden-matches.json: its entries cover every non-text golden leaf element
// and skip exactly the two type: "text" ones (text_title, text_subtitle).
function isMatchable(element: Element): boolean {
  return element.type !== "text";
}

// The signal that a parent's children are a composite group (see
// composite-visual-signal.ts) rather than an ordinary layout grouping: an
// explicit flag (element-tree.schema.json's `composite`), set by whoever
// authors the tree (originally the hand-labeled golden fixture; the planned
// Figma-plugin selection UI's "Combine" action going forward), not inferred
// from geometry. Composite layers are NOT guaranteed to share an identical
// rect - button_x's real Figma geometry (frame/base/icon, corrected from
// earlier placeholder identical rects, see HANDOFF.md) turned out to be 3
// different, nested rects, which silently broke an earlier rect-equality
// version of this check. Ordinary grouping containers (a row/column of
// distinct sub-elements) keep the plain per-element path via recursion.
function isCompositeGroup(element: Element): boolean {
  return element.composite === true && element.children.length > 1;
}

// A single Figma node can correspond to more than one catalog asset - one
// real example: the mockup's "button_x" close button is ONE Figma layer but
// is actually assembled in Unity from three separate sprites (frame + a
// backing layer + the X icon glyph), discovered while investigating why the
// matcher couldn't find a single asset that looked right for it.
// element-tree.schema.json already supports this via `children` (a child is
// just another full element, recursively) - no signal-function changes
// needed, candidates()/structuralSignal()/visualSignal()/gate() are all
// already element-shape-agnostic. What's needed is this: an element WITH
// children is treated as a pure grouping container and is not matched
// directly (matching "the whole composite" against one catalog entry
// doesn't make sense) - only its children (recursively) are matched. A
// childless element is matched directly, same as before this existed.
//
// Composite children (marked via the explicit `composite` flag, e.g.
// button_x's three) additionally need JOINT visual scoring, not just
// independent per-child matching - cropElementFromFrame crops by rect, so
// naively all of them would get the identical full-composite crop (all
// layers visible at once), and comparing that against any ONE child's
// isolated candidate render is comparing a whole picture to one of its
// layers. isCompositeGroup/compositeVisualSignals (composite-visual-
// signal.ts) handle this: structuralSignal still runs per-child as normal
// (text-based, unaffected by the shared crop), but visual scoring
// composites candidate renders from ALL of a group's children together and
// compares the WHOLE composite against the shared crop. Ordinary grouping
// containers (not flagged composite) don't have this problem and keep the
// plain per-element path via recursion.
//
// KNOWN GAP, not fixed here: compositeVisualSignals still derives its
// shared crop/target size from children[0].rect alone (composite-visual-
// signal.ts), i.e. it still implicitly assumes the first child's rect
// approximates the whole group's bounds. That held by coincidence for
// button_x's original placeholder rects but no longer holds exactly for its
// corrected real ones (see HANDOFF.md) - correct today only because the
// children happen to be concentric/overlapping enough not to visibly break
// scoring. A composite group whose layers extend in genuinely different
// directions (not just different sizes) would need that function to crop
// from the union of all children's rects instead - real follow-on work,
// out of scope for this change (detection only).
//
// This does NOT make reduce.ts itself produce decomposed children for
// composite templates like this automatically - it doesn't know which
// Figma component instances need decomposing. That's real, unsolved future
// work (see HANDOFF.md) - this only makes the matcher correctly handle a
// tree that already has children populated and explicitly flagged (as the
// hand-labeled golden fixture now does for button_x, and the planned
// Figma-plugin selection UI's "Combine" action will do going forward).
type WorkItem = { kind: "single"; element: Element } | { kind: "composite"; children: Element[] };

function collectWorkItems(elements: Element[]): WorkItem[] {
  const result: WorkItem[] = [];
  for (const element of elements) {
    if (element.children.length > 0) {
      if (isCompositeGroup(element)) {
        result.push({ kind: "composite", children: element.children.filter(isMatchable) });
      } else {
        result.push(...collectWorkItems(element.children));
      }
    } else if (isMatchable(element)) {
      result.push({ kind: "single", element });
    }
  }
  return result;
}

function workItemSize(item: WorkItem): number {
  return item.kind === "composite" ? item.children.length : 1;
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
  const total = workItems.reduce((sum, item) => sum + workItemSize(item), 0);
  const results: MatchResult = [];
  let done = 0;

  // Sequential across elements/groups, parallel (bounded by topK, ~10)
  // across candidates within one element - keeps peak concurrent work
  // bounded without needing a general-purpose concurrency limiter, which a
  // ~10-15-element slice-scale run doesn't need.
  for (const item of workItems) {
    if (item.kind === "composite") {
      // Composite group (see composite-visual-signal.ts): children stacked
      // at the same rect, so each child's visual score has to come from how
      // the whole group looks composited together, not from comparing one
      // isolated layer's render against the full-group crop.
      const candidatesPerChild = item.children.map((child) => candidates(child, catalog, options.topK));
      const structuralPerChild = candidatesPerChild.map((cands, i) =>
        cands.map((candidate) => structuralSignal(item.children[i], candidate)),
      );
      const visualPerChild = await compositeVisualSignals(
        item.children,
        frame,
        candidatesPerChild,
        structuralPerChild,
        options,
      );
      for (let i = 0; i < item.children.length; i++) {
        const scoredCandidates: ScoredCandidate[] = candidatesPerChild[i].map((candidate, k) => ({
          candidate,
          structural: structuralPerChild[i][k],
          visual: visualPerChild[i][k],
        }));
        results.push(gate(item.children[i], scoredCandidates));
        done++;
        options.onProgress?.(done, total, item.children[i].id);
      }
    } else {
      const element = item.element;
      const topCandidates = candidates(element, catalog, options.topK);
      const scoredCandidates: ScoredCandidate[] = await Promise.all(
        topCandidates.map(async (candidate) => ({
          candidate,
          structural: structuralSignal(element, candidate),
          visual: await visualSignal(element, frame, candidate, options),
        })),
      );
      results.push(gate(element, scoredCandidates));
      done++;
      options.onProgress?.(done, total, element.id);
    }
  }

  return results;
}
