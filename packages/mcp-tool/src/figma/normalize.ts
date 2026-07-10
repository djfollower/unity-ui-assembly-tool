// T1.10: R3 coordinate conversion. Reads the project's CanvasScaler settings
// (hardcoded config for the slice, no live Editor query) and converts frame
// coordinates to reference space.
//
// Uses Unity's own CanvasScaler "Scale With Screen Size" / match-width-or-
// height formula (log2-interpolated blend of width and height ratios),
// applied between source_frame (the mockup's own pixel dimensions) and
// canvas_reference (the project's configured reference resolution) - the
// same formula Unity uses at runtime between the actual device screen and
// the reference resolution, just applied one level up: it's exactly "a
// uniform scale between two differently-proportioned rects, blended by a
// match value," which is what's needed here too.
//
// Output keeps the y-down-from-frame-top-left convention parse-tree.ts and
// fixtures/golden-elements.json already use - Gate 1 scoring diffs this
// output against golden-elements.json, so they must agree. Converting to
// Unity's own RectTransform convention (y-up, anchor/pivot-relative) is
// NodeBuilder.cs's concern (T3.2, Week 3), not this step's.

import type { ElementTree } from "@ui-assembler-slice/contracts";
import type { ReducedElement } from "./reduce.js";

export interface CanvasScalerConfig {
  referenceResolution: { w: number; h: number };
  matchMode: "match_width_or_height" | "expand" | "shrink";
  matchValue: number;
}

export interface FrameMeta {
  frameId: string;
  sourceFrame: { w: number; h: number };
}

export function normalize(
  elements: ReducedElement[],
  frame: FrameMeta,
  canvasScaler: CanvasScalerConfig,
): ElementTree {
  const scale = computeScaleFactor(frame.sourceFrame, canvasScaler);

  return {
    source: "figma",
    frame_id: frame.frameId,
    source_frame: frame.sourceFrame,
    canvas_reference: canvasScaler.referenceResolution,
    canvas_match_mode: canvasScaler.matchMode,
    canvas_match_value: canvasScaler.matchValue,
    elements: elements.map((el) => scaleElement(el, scale)),
  };
}

function computeScaleFactor(
  sourceFrame: { w: number; h: number },
  canvasScaler: CanvasScalerConfig,
): number {
  if (canvasScaler.matchMode !== "match_width_or_height") {
    throw new Error(
      `normalize: canvas_match_mode "${canvasScaler.matchMode}" is not implemented for this slice ` +
      `(only match_width_or_height, matching the fixture's actual CanvasScaler setting)`,
    );
  }

  const logWidth = Math.log2(sourceFrame.w / canvasScaler.referenceResolution.w);
  const logHeight = Math.log2(sourceFrame.h / canvasScaler.referenceResolution.h);
  const logWeightedAverage = logWidth + (logHeight - logWidth) * canvasScaler.matchValue;
  return 2 ** logWeightedAverage;
}

function scaleElement(el: ReducedElement, scale: number): ReducedElement {
  // scale = f(sourceFrame / referenceResolution), mirroring Unity's own
  // scaleFactor = f(screenPixels / referenceResolution) - and since Unity's
  // real relationship is screenPixels = referenceUnits * scaleFactor,
  // converting the other way (mockup pixels -> reference units) divides.
  return {
    ...el,
    rect: {
      x: el.rect.x / scale,
      y: el.rect.y / scale,
      w: el.rect.w / scale,
      h: el.rect.h / scale,
    },
    children: el.children.map((child) => scaleElement(child, scale)),
  };
}
