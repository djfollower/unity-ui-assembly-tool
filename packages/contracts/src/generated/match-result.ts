/**
 * Per-element matcher verdict, produced by the MCP tool's two-factor gate (R5) and consumed by the Unity assembler.
 */
export type MatchResult = MatchResult1[];

export interface MatchResult1 {
  /**
   * References element-tree.json's elements[].id.
   */
  element_id: string;
  /**
   * fallback_eligible: a composite (Combine) group scored below the missing floor against the existing catalog, but a hi-res capture of the group was taken in the Figma plugin at Combine-confirm time - offered for import as a new catalog entry instead of a plain miss.
   */
  status: "matched" | "uncertain" | "missing" | "fallback_eligible";
  /**
   * References catalog-entry.json's id; null when status is missing or fallback_eligible.
   */
  matched_asset_id: string | null;
  signals: {
    visual: number;
    structural: number;
    agree: boolean;
    /**
     * Score margin over the runner-up candidate (R5).
     */
    margin: number;
  };
  /**
   * Present when matched_asset_id's native size differs from the element's rect (R14).
   */
  resize?: {
    /**
     * true for Sliced/Tiled auto-resize; false when a Simple-type asset would distort beyond tolerance.
     */
    safe: boolean;
    size_delta_to?: {
      w: number;
      h: number;
    };
  };
}
