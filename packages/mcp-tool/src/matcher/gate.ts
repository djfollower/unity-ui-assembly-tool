// T2.5: two-factor + margin (R5). Combines visual + structural signals:
// agreement check, margin-over-runner-up check, emits matched/uncertain/missing
// per match-result.schema.json. Includes the resize-safety check (R14).

import type { CatalogEntry, ElementTree, MatchResult } from "@ui-assembler-slice/contracts";

export function gate(
  _element: ElementTree["elements"][number],
  _scoredCandidates: Array<{ candidate: CatalogEntry[number]; visual: number; structural: number }>,
): MatchResult[number] {
  throw new Error("gate: not implemented (T2.5)");
}
