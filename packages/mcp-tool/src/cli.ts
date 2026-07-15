#!/usr/bin/env node
// Entry point for the MCP tool side of the pipeline. No MCP server wrapper
// yet for the slice — this is invoked directly (see scripts/ in repo root).
// Subcommands land as each week's tasks complete:
//   fetch-frame | reduce | reduce-from-selection | build-catalog-descriptions | match | figma-node-rect

import { existsSync, readFileSync } from "node:fs";
import { readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import type { ElementTree, MatchResult } from "@ui-assembler-slice/contracts";
import { loadCatalog } from "./catalog/load-catalog.js";
import { matchElementTree } from "./matcher/match.js";
import { fetchFrame, cachePathFor, type FigmaNode } from "./figma/fetch-frame.js";
import { computeNodeRect } from "./figma/node-rect.js";
import { parseTree, type IntermediateNode } from "./figma/parse-tree.js";
import { reduce } from "./figma/reduce.js";
import { reduceFromSelection } from "./figma/reduce-from-selection.js";
import { normalize } from "./figma/normalize.js";

// Hardcoded to match the real Melon project's CanvasScaler setting - same
// pin fixtures/golden-elements.json uses (source_frame comes from the
// fetched frame's own absoluteBoundingBox at runtime; only the scaler
// config is fixed). normalize.ts documents why this is not a live query.
const CANVAS_SCALER_CONFIG = {
  referenceResolution: { w: 1206, h: 2622 },
  matchMode: "match_width_or_height" as const,
  matchValue: 1,
};

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

async function runFigmaNodeRect(args: string[]): Promise<void> {
  const [nodeId, elementTreePathArg] = args;
  if (!nodeId) {
    console.error("Usage: ui-assembler figma-node-rect <figma-node-id> [element-tree.json]");
    process.exit(1);
  }

  loadEnvFile(path.join(repoRoot(), ".env"));

  const elementTreePath = elementTreePathArg ?? path.join(repoRoot(), "fixtures", "golden-elements.json");
  const elementTree = JSON.parse(await readFile(elementTreePath, "utf8")) as ElementTree;

  const fileKey = process.env.FIGMA_FILE_KEY;
  if (!fileKey) {
    console.error("figma-node-rect: FIGMA_FILE_KEY not set in .env");
    process.exit(1);
  }

  // forceRefresh defaults to false - reads the already-populated
  // .cache/figma/<fileKey>/<nodeId>.json (see fetch-frame.ts's own comment
  // on why this never hits the metered REST API in the normal case this
  // helper exists for: re-deriving a rect for a frame that's already cached).
  const frameNode = await fetchFrame({
    fileKey,
    nodeId: elementTree.frame_id,
    accessToken: process.env.FIGMA_ACCESS_TOKEN ?? "",
  });

  const rect = computeNodeRect(frameNode, nodeId, {
    referenceResolution: elementTree.canvas_reference,
    matchMode: elementTree.canvas_match_mode,
    matchValue: elementTree.canvas_match_value,
  });

  const round2 = (n: number): number => Math.round(n * 100) / 100;
  console.log(JSON.stringify({
    x: round2(rect.x),
    y: round2(rect.y),
    w: round2(rect.w),
    h: round2(rect.h),
  }));
}

function readEnvOrFail(name: string): string {
  const value = process.env[name];
  if (!value) {
    console.error(`${name} not set (check .env / .env.example)`);
    process.exit(1);
  }
  return value;
}

async function runFetchFrame(args: string[]): Promise<void> {
  loadEnvFile(path.join(repoRoot(), ".env"));
  const forceRefresh = args.includes("--refresh");
  const fileKey = readEnvOrFail("FIGMA_FILE_KEY");
  const nodeId = readEnvOrFail("FIGMA_NODE_ID");
  // Access token only required when actually hitting the network - the
  // cached path (the common case, see fetch-frame.ts's own comment) needs
  // nothing beyond the file/node id.
  const accessToken = process.env.FIGMA_ACCESS_TOKEN ?? "";
  if (forceRefresh && !accessToken) {
    console.error("--refresh requested but FIGMA_ACCESS_TOKEN not set");
    process.exit(1);
  }

  const cachePath = cachePathFor(fileKey, nodeId);
  const node = await fetchFrame({ fileKey, nodeId, accessToken, forceRefresh });
  console.log(`fetched "${node.name}" (${nodeId}) -> ${cachePath}`);
}

async function runReduce(args: string[]): Promise<void> {
  loadEnvFile(path.join(repoRoot(), ".env"));
  const outputPathArg = args.find((a) => !a.startsWith("--"));
  const outputPath = outputPathArg ?? path.join(repoRoot(), ".cache", "element-tree.json");

  const fileKey = readEnvOrFail("FIGMA_FILE_KEY");
  const nodeId = readEnvOrFail("FIGMA_NODE_ID");

  const frameNode = await fetchFrame({
    fileKey,
    nodeId,
    accessToken: process.env.FIGMA_ACCESS_TOKEN ?? "",
  });

  const bbox = frameNode.absoluteBoundingBox;
  if (!bbox) {
    console.error(`reduce: frame node ${nodeId} has no absoluteBoundingBox`);
    process.exit(1);
  }

  const intermediate = parseTree(frameNode);
  console.error(`parsed ${intermediate.name} - reducing via agent adapter...`);
  const reduced = await reduce(intermediate);
  console.error(`reduced to ${reduced.length} element(s) - normalizing`);
  const elementTree = normalize(reduced, {
    frameId: nodeId,
    sourceFrame: { w: bbox.width, h: bbox.height },
  }, CANVAS_SCALER_CONFIG);

  await writeFile(outputPath, JSON.stringify(elementTree, null, 2));
  console.log(`element-tree.json: ${outputPath}`);
}

// Walked independently of reduceFromSelection() (which doesn't need to know
// about thumbnails - single responsibility) - collects every node carrying
// a plugin-captured preview image, keyed by its own Figma node id. That's
// the key every emitted ReducedElement's figma_node_id traces back to
// (directly, or via a composite group's anchor), so the Unity review
// window can join a match-result row back to a preview without needing to
// know anything about how reduceFromSelection restructured the tree.
function collectThumbnails(node: IntermediateNode, into: Record<string, string>): void {
  if (node.visible === false) return;
  if (node.thumbnail) into[node.id] = node.thumbnail;
  for (const child of node.children) collectThumbnails(child, into);
}

// Deterministic, non-LLM alternative to `reduce` - consumes the
// figma-plugin/ checkbox tree's downloaded export directly (a standalone
// file the user moves into place themselves, same as reduce/fetch-frame's
// .cache/figma/... convention, but no Figma API/network involvement at
// this stage at all, so no .env / access token needed here).
async function runReduceFromSelection(args: string[]): Promise<void> {
  const [inputPathArg, outputPathArg] = args;
  if (!inputPathArg) {
    console.error("Usage: ui-assembler reduce-from-selection <plugin-export.json> [output.json]");
    process.exit(1);
  }

  const outputPath = outputPathArg ?? path.join(repoRoot(), ".cache", "element-tree.json");

  const root = JSON.parse(await readFile(inputPathArg, "utf8")) as FigmaNode;

  const bbox = root.absoluteBoundingBox;
  if (!bbox) {
    console.error(`reduce-from-selection: root node ${root.id} has no absoluteBoundingBox`);
    process.exit(1);
  }

  const intermediate = parseTree(root);
  const reduced = reduceFromSelection(intermediate);
  console.error(`reduced to ${reduced.length} top-level element(s) - normalizing`);
  const elementTree = normalize(reduced, {
    frameId: root.id,
    sourceFrame: { w: bbox.width, h: bbox.height },
  }, CANVAS_SCALER_CONFIG);

  await writeFile(outputPath, JSON.stringify(elementTree, null, 2));
  console.log(`element-tree.json: ${outputPath}`);

  // Fixed path, not parameterized - the review window expects it there,
  // matching how element-tree.json/match-result.json already have fixed
  // conventional .cache/ locations elsewhere in this CLI.
  const thumbnails: Record<string, string> = {};
  collectThumbnails(intermediate, thumbnails);
  const thumbnailsPath = path.join(repoRoot(), ".cache", "element-thumbnails.json");
  await writeFile(thumbnailsPath, JSON.stringify(thumbnails, null, 2));
  console.log(`element-thumbnails.json: ${thumbnailsPath} (${Object.keys(thumbnails).length} entries)`);
}

const [, , command, ...args] = process.argv;

switch (command) {
  case "fetch-frame":
    await runFetchFrame(args);
    break;
  case "reduce":
    await runReduce(args);
    break;
  case "reduce-from-selection":
    await runReduceFromSelection(args);
    break;
  case "match":
    await runMatch(args);
    break;
  case "figma-node-rect":
    await runFigmaNodeRect(args);
    break;
  default:
    console.error(`Unknown or unimplemented command: ${command ?? "(none)"}`);
    console.error("Commands: fetch-frame [--refresh] | reduce [output.json] | reduce-from-selection <plugin-export.json> [output.json] | match <element-tree.json> <catalog.json> [output.json] | figma-node-rect <figma-node-id> [element-tree.json]");
    process.exit(1);
}
