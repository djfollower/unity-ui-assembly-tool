// T1.8: maps raw Figma nodes (geometry, hierarchy, fills, effects, text vs.
// image, component instance, constraints) into an intermediate structure,
// pre-reduction. Structural only - reads Figma's JSON node tree (from
// fetch-frame.ts), no image/vision analysis of the rendered mockup.

import type { FigmaNode } from "./fetch-frame.js";

export interface IntermediateNode {
  id: string;
  name: string;
  figmaType: string; // FRAME | TEXT | INSTANCE | RECTANGLE | GROUP | ...
  visible: boolean;
  /** Frame-relative pixel rect (absoluteBoundingBox minus the frame's own origin). */
  rect: { x: number; y: number; w: number; h: number };
  isComponentInstance: boolean;
  textContent?: string;
  hasEffects: boolean;
  effectTypes: string[];
  // Carried through unchanged from FigmaNode when present (figma-plugin/
  // exports only - see reduce-from-selection.ts, the consumer of these).
  // parseTree stays pure/mechanical either way - it doesn't interpret them.
  selected?: boolean;
  typeTag?: string;
  compositeGroupId?: string;
  thumbnail?: string;
  children: IntermediateNode[];
}

export function parseTree(frameNode: FigmaNode): IntermediateNode {
  const origin = frameNode.absoluteBoundingBox;
  if (!origin) {
    throw new Error(`parseTree: frame node ${frameNode.id} has no absoluteBoundingBox`);
  }
  return mapNode(frameNode, origin);
}

function mapNode(
  node: FigmaNode,
  origin: { x: number; y: number },
): IntermediateNode {
  const box = node.absoluteBoundingBox;
  const effects = (node.effects ?? []).filter((e) => e.visible !== false);

  return {
    id: node.id,
    name: node.name,
    figmaType: node.type,
    visible: node.visible !== false,
    rect: box
      ? { x: box.x - origin.x, y: box.y - origin.y, w: box.width, h: box.height }
      : { x: 0, y: 0, w: 0, h: 0 },
    isComponentInstance: node.type === "INSTANCE",
    textContent: node.type === "TEXT" ? node.characters : undefined,
    hasEffects: effects.length > 0,
    effectTypes: effects.map((e) => e.type),
    selected: node.selected,
    typeTag: node.typeTag,
    compositeGroupId: node.compositeGroupId,
    thumbnail: node.thumbnail,
    children: (node.children ?? []).map((child) => mapNode(child, origin)),
  };
}
