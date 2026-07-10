// T2.4: layer-name-to-asset-name similarity, component-instance-to-prefab
// match, description similarity, combined into a single structural score.

import type { CatalogEntry, ElementTree } from "@ui-assembler-slice/contracts";

export function structuralSignal(
  _element: ElementTree["elements"][number],
  _candidate: CatalogEntry[number],
): number {
  throw new Error("structuralSignal: not implemented (T2.4)");
}
