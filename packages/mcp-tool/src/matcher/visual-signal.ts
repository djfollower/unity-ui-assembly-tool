// T2.3: render-aware comparison, the fix for the 9-slice/tint problem. For
// each (element, candidate) pair, renders the candidate at the element's size
// using its render metadata (border, PPU x multiplier, tint), then a Claude
// multimodal call scores similarity 0-1.

import type { CatalogEntry, ElementTree } from "@ui-assembler-slice/contracts";

export async function visualSignal(
  _element: ElementTree["elements"][number],
  _candidate: CatalogEntry[number],
): Promise<number> {
  throw new Error("visualSignal: not implemented (T2.3)");
}
