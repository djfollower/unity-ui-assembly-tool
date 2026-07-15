/**
 * Reduced, normalized Figma frame produced by the MCP tool's parse + reduce + normalize steps.
 */
export interface ElementTree {
  source: "figma";
  /**
   * Figma frame node id.
   */
  frame_id: string;
  source_frame: Size;
  canvas_reference: Size1;
  /**
   * Mirrors the project's CanvasScaler screen match mode.
   */
  canvas_match_mode: "match_width_or_height" | "expand" | "shrink";
  /**
   * CanvasScaler match slider value, used by normalize.ts (R3).
   */
  canvas_match_value: number;
  elements: Element[];
}
/**
 * Pixel dimensions of the source Figma frame.
 */
export interface Size {
  w: number;
  h: number;
}
/**
 * Reference resolution the frame is normalized into (R3).
 */
export interface Size1 {
  w: number;
  h: number;
}
export interface Element {
  /**
   * Derived from the Figma layer name.
   */
  id: string;
  figma_node_id: string;
  /**
   * Logical element type assigned by the reduction pass, e.g. button, label, image, panel.
   */
  type: string;
  rect: Rect;
  visual_description: string;
  /**
   * Present only if a child text node exists.
   */
  text_content?: string;
  /**
   * Figma signal used as a template hint (R9).
   */
  is_component_instance: boolean;
  children: Element[];
  /**
   * True when this element's children are several layers that combine into one visual (e.g. a frame + backing + glyph stacked to form one button), to be scored jointly by the matcher rather than independently. Explicit, not inferred from geometry - composite layers are not guaranteed to share an identical rect. Absent/false means an ordinary layout grouping.
   */
  composite?: boolean;
  /**
   * True when this element's children should be built as real nested GameObjects under a real parent transform (e.g. for post-assembly animation), rather than flattened to siblings under the canvas root. Explicit, author-set - absent/false means flatten (today's existing behavior). Independent of `composite` - a group can be composite, container, both, or neither.
   */
  container?: boolean;
}
/**
 * Position/size in canvas_reference space, post-normalization (R3).
 */
export interface Rect {
  x: number;
  y: number;
  w: number;
  h: number;
}
