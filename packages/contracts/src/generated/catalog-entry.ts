/**
 * One sprite or prefab asset, probed by the Unity Editor batch step (R17: Editor script is authoritative for render metadata).
 */
export type CatalogEntry = CatalogEntry1[];

export interface CatalogEntry1 {
  id: string;
  /**
   * Asset path relative to the Unity project, e.g. Assets/Common/Prefabs/RedButton.prefab.
   */
  path: string;
  type: "sprite" | "prefab";
  /**
   * Feature folder this asset belongs to (single-feature retrieval scope for the slice, R11).
   */
  feature: string;
  /**
   * Render metadata read live via Unity's asset APIs (R14, R17).
   */
  render: {
    /**
     * Mirrors UnityEngine.UI.Image.Type.
     */
    image_type: "Simple" | "Sliced" | "Tiled" | "Filled";
    /**
     * Sprite 9-slice border: [left, bottom, right, top].
     *
     * @minItems 4
     * @maxItems 4
     */
    border: [number, number, number, number];
    ppu: number;
    ppu_multiplier: number;
    native_size: {
      w: number;
      h: number;
    };
    /**
     * Hex color applied to the Image/SpriteRenderer, or null if untinted.
     */
    tint: string | null;
  };
  /**
   * Base64 data URI of the RENDERED asset (at canonical size, post 9-slice/tint), not the raw texture (R14).
   */
  thumbnail: string;
  /**
   * e.g. base, pressed, disabled — asset's role within its template family.
   */
  role: string;
  interactive: boolean;
  /**
   * LLM-generated description, computed off the rendered thumbnail, not the raw asset.
   */
  visual_description: string;
}
