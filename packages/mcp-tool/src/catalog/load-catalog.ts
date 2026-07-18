// Reads catalog.json produced by the Unity batch RunCatalogBuild step
// (scripts/build-catalog.sh -> RunCatalogBuild.cs, T1.6) - the MCP tool
// side of the two-process, JSON-file-only handoff (no live bridge, see
// README's Data Flow section).

import { readFile } from "node:fs/promises";
import path from "node:path";
import type { CatalogEntry } from "@ui-assembler-slice/contracts";

// `thumbnail_path` on disk is relative to catalog.json's OWN directory (not
// the repo root or the Unity project - see the schema's description), so a
// catalog built on one machine and copied elsewhere still resolves. This is
// the one chokepoint every catalog consumer loads through (cli.ts is the
// only caller), so it's resolved to an absolute path here, once - nothing
// downstream (render-candidate.ts et al.) needs to know where catalog.json
// physically lives.
export async function loadCatalog(catalogJsonPath: string): Promise<CatalogEntry> {
  const raw = await readFile(catalogJsonPath, "utf8");
  const entries = JSON.parse(raw) as CatalogEntry;
  const catalogDir = path.dirname(catalogJsonPath);
  return entries.map((entry) => ({
    ...entry,
    thumbnail_path: path.resolve(catalogDir, entry.thumbnail_path),
  })) as CatalogEntry;
}
