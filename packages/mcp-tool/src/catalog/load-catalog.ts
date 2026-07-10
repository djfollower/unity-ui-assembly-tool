// Reads catalog.json produced by the Unity batch RunCatalogBuild step.

import type { CatalogEntry } from "@ui-assembler-slice/contracts";

export async function loadCatalog(_catalogJsonPath: string): Promise<CatalogEntry> {
  throw new Error("loadCatalog: not implemented");
}
