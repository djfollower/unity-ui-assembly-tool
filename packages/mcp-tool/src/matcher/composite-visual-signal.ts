// Fix for the "composite children share one crop" gap (see HANDOFF.md):
// button_x-style composite elements are one Figma layer that the real Unity
// asset actually assembles from several sprites stacked at the SAME rect
// (button_x_frame/button_x_base/button_x_icon). cropElementFromFrame crops
// by rect, so all three children get the identical full-composite crop
// (frame+base+icon all visible at once) - comparing that against any ONE
// child's isolated candidate render (visual-signal.ts's normal per-element
// path) is comparing a whole picture to one of its layers, which is
// structurally unfair to every candidate regardless of correctness.
//
// Fix: score each child's candidates by how good the WHOLE GROUP looks once
// composited together, not how good that one layer looks alone against the
// full crop. Coordinate-ascent / Gauss-Seidel local search, not exhaustive
// search over all combinations (topK^childCount candidate tuples - 10^3 for
// this fixture alone, would only get worse with more children/topK):
// initialize every child's "current pick" from its own structural signal
// (unaffected by the crop-sharing bug, since it's text-based), then for a
// few passes, revisit each child in turn, holding every OTHER child's
// current pick fixed, and score EACH of this child's own topK candidates by
// compositing it into the group (replacing this child's slot) and comparing
// the resulting full composite against the shared crop - then move this
// child's pick to whichever candidate scored best, immediately visible to
// later children in the same pass (Gauss-Seidel, not Jacobi - converges
// faster for a handful of elements). The LAST pass's per-candidate scores
// for each child are returned as that child's visual signal array, aligned
// index-for-index with the candidates() array passed in for it - callers
// feed these straight into gate.ts's ScoredCandidate the same way a normal
// per-element visualSignal() score would be.
//
// Composite order (which layer draws on top of which) is assumed to be the
// children array's own order, first = bottom-most, last = top-most - this
// matches how golden-elements.json lists button_x's children (frame, then
// base, then icon on top, which is also the only order that looks right).
// Not independently verified beyond this one fixture case; revisit if a
// composite group ever needs an explicit z-order field instead.

import sharp from "sharp";
import type { CatalogEntry, ElementTree } from "@ui-assembler-slice/contracts";
import { cropElementFromFrame, type FrameMeta } from "./element-crop.js";
import { renderCandidateAtSize } from "./render-candidate.js";
import { compareImages } from "./visual-signal.js";

export interface CompositeVisualSignalOptions {
  frameExportPath?: string;
  passes?: number;
}

const DEFAULT_PASSES = 2;

async function composeLayers(layers: Buffer[], target: { w: number; h: number }): Promise<Buffer> {
  const width = Math.max(1, Math.round(target.w));
  const height = Math.max(1, Math.round(target.h));
  return sharp({ create: { width, height, channels: 4, background: { r: 0, g: 0, b: 0, alpha: 0 } } })
    .composite(layers.map((input) => ({ input, left: 0, top: 0 })))
    .png()
    .toBuffer();
}

function argmax(values: number[]): number {
  let best = 0;
  for (let i = 1; i < values.length; i++) {
    if (values[i] > values[best]) best = i;
  }
  return best;
}

// children and candidatesPerChild/structuralPerChild are index-aligned:
// children[i]'s candidate pool is candidatesPerChild[i], whose structural
// scores are structuralPerChild[i]. Returns a visual score per child per
// candidate, same shape as candidatesPerChild - one array per child, one
// number per that child's candidate, in the same order.
export async function compositeVisualSignals(
  children: ElementTree["elements"][number][],
  frame: FrameMeta,
  candidatesPerChild: CatalogEntry[],
  structuralPerChild: number[][],
  options: CompositeVisualSignalOptions = {},
): Promise<number[][]> {
  const passes = options.passes ?? DEFAULT_PASSES;
  const target = children[0].rect;
  const sharedCrop = await cropElementFromFrame(children[0], frame, options.frameExportPath);

  const renderCache = new Map<string, Promise<Buffer>>();
  function renderOf(candidate: CatalogEntry[number]): Promise<Buffer> {
    let cached = renderCache.get(candidate.id);
    if (!cached) {
      cached = renderCandidateAtSize(candidate, target);
      renderCache.set(candidate.id, cached);
    }
    return cached;
  }

  const currentPickIdx = structuralPerChild.map((scores) => argmax(scores));
  const perChildVisual: number[][] = candidatesPerChild.map((cands) => new Array(cands.length).fill(0));

  for (let pass = 0; pass < passes; pass++) {
    for (let i = 0; i < children.length; i++) {
      const scores = await Promise.all(
        candidatesPerChild[i].map(async (candidate) => {
          const layers = await Promise.all(
            children.map((_, j) => renderOf(j === i ? candidate : candidatesPerChild[j][currentPickIdx[j]])),
          );
          const composite = await composeLayers(layers, target);
          return compareImages(sharedCrop, composite);
        }),
      );
      perChildVisual[i] = scores;
      currentPickIdx[i] = argmax(scores);
    }
  }

  return perChildVisual;
}
