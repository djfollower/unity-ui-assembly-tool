// T2.2: tiered retrieval, single-feature scope for the slice (tiering across
// features deferred, R11). Coarse pre-filter by name/description similarity
// before the two-factor gate scores candidates properly.

import type { CatalogEntry, ElementTree } from "@ui-assembler-slice/contracts";

export function candidates(
  _element: ElementTree["elements"][number],
  _catalog: CatalogEntry,
): CatalogEntry {
  throw new Error("candidates: not implemented (T2.2)");
}
