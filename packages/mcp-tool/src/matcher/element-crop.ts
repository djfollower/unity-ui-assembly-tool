// T2.3 support: crops an element's region out of fixtures/frame-export.png
// for use as the visual "ground truth" in visual-signal.ts.
//
// Why crop a cached frame render instead of calling Figma's image-export API
// per element: the free-tier Figma REST quota is 6 requests/month (see
// fetch-frame.ts) - exporting N per-element PNGs would burn it in one run.
// frame-export.png is a single cached render of the whole frame at
// source_frame pixel dimensions; every element's crop comes from slicing
// that one file locally.
//
// element.rect is post-normalize.ts (canvas_reference space, R3) but
// frame-export.png is in source_frame pixel space - this inverts that same
// scale factor to map back.

import { readFile } from "node:fs/promises";
import path from "node:path";
import sharp from "sharp";
import type { ElementTree } from "@ui-assembler-slice/contracts";
import { computeScaleFactor, type CanvasScalerConfig } from "../figma/normalize.js";

export type FrameMeta = Pick<ElementTree, "source_frame" | "canvas_reference" | "canvas_match_mode" | "canvas_match_value">;

function defaultFrameExportPath(): string {
  // packages/mcp-tool/src/matcher/ -> repo root is four levels up.
  return path.join(import.meta.dirname, "..", "..", "..", "..", "fixtures", "frame-export.png");
}

function defaultSyntheticDir(): string {
  return path.join(import.meta.dirname, "..", "..", "..", "..", "fixtures", "synthetic");
}

function toSourceFrameRect(
  rect: { x: number; y: number; w: number; h: number },
  frame: FrameMeta,
): { x: number; y: number; w: number; h: number } {
  const canvasScaler: CanvasScalerConfig = {
    referenceResolution: frame.canvas_reference,
    matchMode: frame.canvas_match_mode,
    matchValue: frame.canvas_match_value,
  };
  const scale = computeScaleFactor(frame.source_frame, canvasScaler);
  return { x: rect.x * scale, y: rect.y * scale, w: rect.w * scale, h: rect.h * scale };
}

// Crops and clamps to the source image bounds (an element's rect can extend
// a fraction of a pixel past the frame edge from rounding - e.g. Scrim's
// 1208x2622 rect against a 1209x2622 export - sharp's extract() throws on an
// out-of-bounds region rather than clamping it itself).
export async function cropElementFromFrame(
  element: Pick<ElementTree["elements"][number], "rect">,
  frame: FrameMeta,
  frameExportPath: string = defaultFrameExportPath(),
): Promise<Buffer> {
  const sourceRect = toSourceFrameRect(element.rect, frame);
  const png = await readFile(frameExportPath);
  const image = sharp(png);
  const meta = await image.metadata();
  const imageW = meta.width ?? 0;
  const imageH = meta.height ?? 0;

  const left = Math.max(0, Math.round(sourceRect.x));
  const top = Math.max(0, Math.round(sourceRect.y));
  const width = Math.max(1, Math.min(Math.round(sourceRect.w), imageW - left));
  const height = Math.max(1, Math.min(Math.round(sourceRect.h), imageH - top));

  return image.extract({ left, top, width, height }).png().toBuffer();
}

// Fix for the "synthetic-element crop quality" gap (see HANDOFF.md): 3 of
// golden-elements.json's top-level elements (icon_gem, button_share,
// button_upgrade) are fabricated for Gate 2 test coverage - their `rect`
// doesn't correspond to any real content in frame-export.png (there was
// never a real design for them), so cropElementFromFrame's real-mockup crop
// is meaningless for them - confirmed to actively cause a false-accept
// (button_upgrade) and a missed-recall (button_share) in a real Gate 2 run,
// not just a theoretical risk.
//
// Fix: for exactly these elements - identified unambiguously by their own
// figma_node_id carrying the synthetic: prefix, which by construction only
// ever appears on top-level fabricated elements (button_x's composite
// children also carry this prefix, but they're never routed through this
// function - match.ts's composite path calls cropElementFromFrame directly,
// since THEIR shared rect IS real content, just decomposed - see match.ts's
// isCompositeGroup) - use a hand-authored fixtures/synthetic/<id>.png
// instead of a frame-export.png crop. These aren't arbitrary placeholder
// art: icon_gem/button_share are deliberately built to NOT resemble any
// real catalog asset (they exist to test missing-recall, per their own
// visual_description) - button_upgrade is deliberately built FROM the real
// UIElements__ButtonFrame catalog asset's own render, multiply-tinted by
// the exact #7349FF hex its visual_description already names as the design
// target (see fixtures/synthetic/README or the generation notes in
// HANDOFF.md) - not circular, since the base geometry/shading comes from a
// DIFFERENT catalog entry than the one being tested for, and the tint value
// was already public information in the fixture's own authored description.
function syntheticCropPath(elementId: string, syntheticDir: string): string {
  return path.join(syntheticDir, `${elementId}.png`);
}

export async function resolveElementCrop(
  element: Pick<ElementTree["elements"][number], "id" | "figma_node_id" | "rect">,
  frame: FrameMeta,
  options: { frameExportPath?: string; syntheticDir?: string } = {},
): Promise<Buffer> {
  if (element.figma_node_id.startsWith("synthetic:")) {
    return readFile(syntheticCropPath(element.id, options.syntheticDir ?? defaultSyntheticDir()));
  }
  return cropElementFromFrame(element, frame, options.frameExportPath);
}
