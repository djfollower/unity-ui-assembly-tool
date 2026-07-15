// T1.8: Figma REST client. Pulls the chosen frame's full node subtree via
// GET /v1/files/:key/nodes?ids=... using a personal access token.
//
// Cache-first, and for a good reason beyond dev convenience: a free-tier
// Figma REST API token is capped at 6 requests/month, so the network path
// here is close to unusable for iteration. The primary way to populate the
// cache is the local dev plugin (packages/mcp-tool/figma-plugin/ - runs
// inside Figma, not network-metered) exporting straight to
// .cache/figma/<fileKey>/<nodeId>.json. fetchFrame reads that same cache
// path first regardless of which path filled it, and only calls the network
// when forceRefresh: true (see scripts/fetch-figma-frame.mjs --refresh) -
// expect to hit the quota fast if you do.

import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";

export interface FetchFrameOptions {
  fileKey: string;
  nodeId: string;
  accessToken: string;
  /** Defaults to <repo root>/.cache/figma */
  cacheDir?: string;
  /** Bypass the cache and re-fetch from the Figma API. */
  forceRefresh?: boolean;
}

// Loose shape - the full Figma node schema is large and only partially used
// downstream (parse-tree.ts picks the fields it needs).
export interface FigmaNode {
  id: string;
  name: string;
  type: string;
  visible?: boolean;
  absoluteBoundingBox?: { x: number; y: number; width: number; height: number };
  characters?: string;
  effects?: Array<{ type: string; visible?: boolean }>;
  children?: FigmaNode[];
  // Present only on exports from the figma-plugin/ checkbox tree (see
  // reduce-from-selection.ts) - absent on plain REST-API-fetched nodes.
  selected?: boolean;
  typeTag?: string;
  compositeGroupId?: string;
  // Base64 PNG data URI captured by the plugin's Stage 1 exportAsync
  // thumbnails - a review-UI preview source, not used by matching itself.
  thumbnail?: string;
  [key: string]: unknown;
}

function defaultCacheDir(): string {
  // packages/mcp-tool/src/figma/ -> repo root is four levels up.
  return path.join(import.meta.dirname, "..", "..", "..", "..", ".cache", "figma");
}

export function cachePathFor(fileKey: string, nodeId: string, cacheDir?: string): string {
  return path.join(cacheDir ?? defaultCacheDir(), fileKey, `${nodeId.replace(":", "-")}.json`);
}

export async function fetchFrame(options: FetchFrameOptions): Promise<FigmaNode> {
  const cachePath = cachePathFor(options.fileKey, options.nodeId, options.cacheDir);

  if (!options.forceRefresh && existsSync(cachePath)) {
    return JSON.parse(readFileSync(cachePath, "utf8")) as FigmaNode;
  }

  const url = `https://api.figma.com/v1/files/${options.fileKey}/nodes?ids=${options.nodeId}`;
  const res = await fetch(url, { headers: { "X-Figma-Token": options.accessToken } });

  if (!res.ok) {
    throw new Error(`Figma API error: ${res.status} ${res.statusText} - ${await res.text()}`);
  }

  const body = (await res.json()) as { nodes?: Record<string, { document: FigmaNode }> };
  const document = body.nodes?.[options.nodeId]?.document;
  if (!document) {
    throw new Error(`Node ${options.nodeId} not found in Figma response for file ${options.fileKey}`);
  }

  mkdirSync(path.dirname(cachePath), { recursive: true });
  writeFileSync(cachePath, JSON.stringify(document, null, 2));

  return document;
}
