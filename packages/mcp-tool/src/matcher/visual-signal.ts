// T2.3: render-aware comparison, the fix for the 9-slice/tint problem.
// Resolves the element's visual "ground truth" via element-crop.ts's
// resolveElementCrop - a crop of fixtures/frame-export.png (the cached frame
// export, not a fresh per-element Figma image-export call) for real
// elements, or a hand-authored fixtures/synthetic/<id>.png for the 3
// fabricated golden elements whose rect doesn't correspond to real frame
// content (see resolveElementCrop's own comment - fixing THIS is what
// closed the "synthetic-element crop quality" gap HANDOFF.md used to flag:
// comparing against real-but-irrelevant pixels at a made-up rect was
// actively causing a false-accept and a missed-recall, not just a
// theoretical risk). Renders the candidate at that same size via
// render-candidate.ts's pure-JS 9-slice renderer (no Unity round-trip), then
// scores similarity with pure local pixel math - no LLM call.
//
// History: this went through two LLM-based designs before landing here.
// First, @anthropic-ai/sdk directly (needed a separate metered
// ANTHROPIC_API_KEY). Then the same claude-CLI agent adapter reduce.ts uses
// (rides on an existing Claude subscription, no separate bill) - reasoned
// at the time as good enough since it avoided a NEW cost. It didn't hold
// up in practice: this signal runs once per (element, candidate) pair, so
// a topK=10 run against ~10 elements means up to ~100 nested `claude`
// invocations, and a real run at that volume burned through a real
// subscription's usage quota mid-run. Unlike reduce.ts (one call per
// pipeline run), this needed to be free to run as often as needed, not
// just cheap. It's now pure local pixel math: SSIM (structural similarity,
// catches wrong template/shape) combined with a color histogram distance
// (catches wrong tint/color - SSIM alone is mostly luminance/structure
// based and can miss a same-shape-wrong-color mismatch, exactly the
// tinted-asset case R14 cares about). No LLM call, no network, no
// subscription usage, runs in milliseconds. `ssim.js` is the one new
// dependency (pure JS, zero transitive deps) - `sharp` was already a
// dependency (render-candidate.ts/element-crop.ts).

import sharp from "sharp";
import { ssim } from "ssim.js";
import type { CatalogEntry, ElementTree } from "@ui-assembler-slice/contracts";
import { resolveElementCrop, type FrameMeta } from "./element-crop.js";
import { renderCandidateAtSize } from "./render-candidate.js";

export interface VisualSignalOptions {
  frameExportPath?: string;
  syntheticDir?: string;
}

// Both images resized to this before comparing - fast (SSIM cost scales
// with pixel count), and fine texture detail doesn't matter for "is this
// the same asset" judgment at this scale.
const COMPARISON_SIZE = 64;
const HISTOGRAM_BINS_PER_CHANNEL = 8;
const WEIGHT_STRUCTURAL = 0.6;
const WEIGHT_COLOR = 0.4;

interface ComparableImage {
  data: Uint8ClampedArray;
  width: number;
  height: number;
}

// Flattens onto a neutral gray background before resizing: the mockup crop
// (from a flat screenshot, frame-export.png) has no transparency, but a
// candidate render can have real transparent padding (e.g. a non-rectangular
// Simple sprite like a heart icon, or letterboxing around a differently-
// shaped candidate) - comparing raw RGBA would let transparent pixels'
// undefined/black-by-convention color data bias the color histogram unfairly
// against otherwise-correct candidates. Flattening onto the SAME neutral
// color on both sides keeps the comparison symmetric (a no-op for the
// already-opaque crop).
async function toComparableImage(png: Buffer, size: number): Promise<ComparableImage> {
  const { data, info } = await sharp(png)
    .flatten({ background: { r: 128, g: 128, b: 128 } })
    .resize(size, size, { fit: "fill" })
    .ensureAlpha()
    .raw()
    .toBuffer({ resolveWithObject: true });
  return {
    data: new Uint8ClampedArray(data.buffer, data.byteOffset, data.length),
    width: info.width,
    height: info.height,
  };
}

// Per-channel (R,G,B) normalized histogram - a coarse but cheap stand-in
// for "are these the same color(s)", robust to the exact pixel layout
// differing slightly (unlike a naive pixel-diff), which matters since the
// crop and render come from genuinely different rendering pipelines
// (a real screenshot vs. a synthetic 9-slice composite).
function colorHistogram(image: ComparableImage): number[] {
  const histogram = new Array(HISTOGRAM_BINS_PER_CHANNEL * 3).fill(0);
  const pixelCount = image.width * image.height;
  for (let i = 0; i < pixelCount; i++) {
    const offset = i * 4;
    for (let channel = 0; channel < 3; channel++) {
      const value = image.data[offset + channel];
      const bin = Math.min(HISTOGRAM_BINS_PER_CHANNEL - 1, Math.floor((value / 256) * HISTOGRAM_BINS_PER_CHANNEL));
      histogram[channel * HISTOGRAM_BINS_PER_CHANNEL + bin] += 1;
    }
  }
  return histogram.map((count) => count / pixelCount);
}

// Histogram intersection, normalized to 0-1 (1 = identical distributions).
function histogramSimilarity(a: number[], b: number[]): number {
  let intersection = 0;
  for (let i = 0; i < a.length; i++) {
    intersection += Math.min(a[i], b[i]);
  }
  return intersection / 3; // 3 channels, each channel's bins sum to 1
}

// SSIM + color-histogram comparison of two already-rendered PNGs (a crop of
// real mockup pixels vs. a synthetic render, or - for composite-visual-
// signal.ts - two synthetic renders against each other). Factored out of
// visualSignal so composite-visual-signal.ts's joint (whole-group-vs-crop)
// comparison can reuse the exact same scoring, not a second copy of it.
export async function compareImages(aPng: Buffer, bPng: Buffer): Promise<number> {
  const [imageA, imageB] = await Promise.all([
    toComparableImage(aPng, COMPARISON_SIZE),
    toComparableImage(bPng, COMPARISON_SIZE),
  ]);

  const structuralScore = ssim(imageA, imageB).mssim;
  const colorScore = histogramSimilarity(colorHistogram(imageA), colorHistogram(imageB));

  return Math.min(1, Math.max(0, structuralScore * WEIGHT_STRUCTURAL + colorScore * WEIGHT_COLOR));
}

export async function visualSignal(
  element: ElementTree["elements"][number],
  frame: FrameMeta,
  candidate: CatalogEntry[number],
  options: VisualSignalOptions = {},
): Promise<number> {
  const [elementCrop, candidateRender] = await Promise.all([
    resolveElementCrop(element, frame, options),
    renderCandidateAtSize(candidate, { w: element.rect.w, h: element.rect.h }),
  ]);

  return compareImages(elementCrop, candidateRender);
}
