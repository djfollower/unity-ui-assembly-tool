// Helper for hand-labeling golden-elements.json (or any other element-tree
// authored by hand rather than by reduce.ts): given a target Figma node id
// found anywhere inside an already-cached frame export, compute its rect in
// canvas_reference space - the same "figmaAbsoluteBoundingBox - frameOrigin,
// then / scale" transform derived and hand-verified while fixing button_x's
// composite children's rects (see HANDOFF.md's "Second review pass"
// section). Exists so a rect for a real Figma node is always pulled through
// this one path instead of being hand-typed/eyeballed - that's exactly how
// those rects went wrong the first time.
//
// Reuses parse-tree.ts's frame-relative rect math and normalize.ts's scale
// factor rather than re-deriving either - a second implementation of the
// same transform is exactly the kind of drift risk this helper exists to
// avoid.

import type { FigmaNode } from "./fetch-frame.js";
import { parseTree, type IntermediateNode } from "./parse-tree.js";
import { computeScaleFactor, type CanvasScalerConfig } from "./normalize.js";

export interface NodeRect {
  x: number;
  y: number;
  w: number;
  h: number;
}

function findById(node: IntermediateNode, targetId: string): IntermediateNode | undefined {
  if (node.id === targetId) return node;
  for (const child of node.children) {
    const found = findById(child, targetId);
    if (found) return found;
  }
  return undefined;
}

export function computeNodeRect(
  frameNode: FigmaNode,
  targetNodeId: string,
  canvasScaler: CanvasScalerConfig,
): NodeRect {
  if (!frameNode.absoluteBoundingBox) {
    throw new Error(`computeNodeRect: frame node ${frameNode.id} has no absoluteBoundingBox`);
  }

  // frame-relative, unscaled (source_frame pixel space)
  const tree = parseTree(frameNode);
  const target = findById(tree, targetNodeId);
  if (!target) {
    throw new Error(`computeNodeRect: node "${targetNodeId}" not found under frame ${frameNode.id}`);
  }

  const scale = computeScaleFactor(
    { w: frameNode.absoluteBoundingBox.width, h: frameNode.absoluteBoundingBox.height },
    canvasScaler,
  );

  return {
    x: target.rect.x / scale,
    y: target.rect.y / scale,
    w: target.rect.w / scale,
    h: target.rect.h / scale,
  };
}
