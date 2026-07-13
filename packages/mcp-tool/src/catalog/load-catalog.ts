// Reads catalog.json produced by the Unity batch RunCatalogBuild step
// (scripts/build-catalog.sh -> RunCatalogBuild.cs, T1.6) - the MCP tool
// side of the two-process, JSON-file-only handoff (no live bridge, see
// README's Data Flow section).

import { readFile } from "node:fs/promises";
import type { CatalogEntry } from "@ui-assembler-slice/contracts";

export async function loadCatalog(catalogJsonPath: string): Promise<CatalogEntry> {
  const raw = await readFile(catalogJsonPath, "utf8");
  return JSON.parse(raw) as CatalogEntry;
}
