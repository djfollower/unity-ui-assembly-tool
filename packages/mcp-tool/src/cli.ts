#!/usr/bin/env node
// Entry point for the MCP tool side of the pipeline. No MCP server wrapper
// yet for the slice — this is invoked directly (see scripts/ in repo root).
// Subcommands land as each week's tasks complete:
//   fetch-frame | reduce | build-catalog-descriptions | match

import { existsSync, readFileSync } from "node:fs";
import { readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import type { ElementTree, MatchResult } from "@ui-assembler-slice/contracts";
import { loadCatalog } from "./catalog/load-catalog.js";
import { matchElementTree } from "./matcher/match.js";

// Same small inline .env loader as scripts/*.mjs (fetch-figma-frame.mjs,
// smoke-test-figma.mjs) - intentional duplication, not worth a "dotenv"
// dependency for one function; see those files' comments.
function loadEnvFile(envPath: string): void {
  if (!existsSync(envPath)) return;
  for (const line of readFileSync(envPath, "utf8").split("\n")) {
    const trimmed = line.trim();
    if (!trimmed || trimmed.startsWith("#")) continue;
    const eq = trimmed.indexOf("=");
    if (eq === -1) continue;
    const key = trimmed.slice(0, eq).trim();
    const value = trimmed.slice(eq + 1).trim();
    if (process.env[key] === undefined) process.env[key] = value;
  }
}

function repoRoot(): string {
  // packages/mcp-tool/src/ -> repo root is three levels up.
  return path.join(import.meta.dirname, "..", "..", "..");
}

async function runMatch(args: string[]): Promise<void> {
  const [elementTreePath, catalogPath, outputPathArg] = args;
  if (!elementTreePath || !catalogPath) {
    console.error("Usage: ui-assembler match <element-tree.json> <catalog.json> [output.json]");
    process.exit(1);
  }

  loadEnvFile(path.join(repoRoot(), ".env"));

  const elementTree = JSON.parse(await readFile(elementTreePath, "utf8")) as ElementTree;
  const catalog = await loadCatalog(catalogPath);
  const outputPath = outputPathArg ?? path.join(repoRoot(), ".cache", "match-result.json");

  const result: MatchResult = await matchElementTree(elementTree, catalog, {
    onProgress: (done, total, elementId) => {
      console.error(`[${done}/${total}] ${elementId}`);
    },
  });

  await writeFile(outputPath, JSON.stringify(result, null, 2));
  console.log(`match-result.json: ${outputPath}`);

  const counts: Record<string, number> = {};
  for (const entry of result) counts[entry.status] = (counts[entry.status] ?? 0) + 1;
  console.log(`matched=${counts.matched ?? 0} uncertain=${counts.uncertain ?? 0} missing=${counts.missing ?? 0}`);
}

const [, , command, ...args] = process.argv;

switch (command) {
  case "match":
    await runMatch(args);
    break;
  default:
    console.error(`Unknown or unimplemented command: ${command ?? "(none)"}`);
    process.exit(1);
}
