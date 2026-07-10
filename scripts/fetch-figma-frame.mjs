#!/usr/bin/env node
// Pulls the chosen frame's full raw node tree from Figma once and caches it
// at .cache/figma/<fileKey>/<nodeId>.json (gitignored). Run this once per
// design change instead of hitting the Figma API on every dev iteration -
// packages/mcp-tool/src/figma/fetch-frame.ts reads from the same cache path.
//
// Usage: node scripts/fetch-figma-frame.mjs [--refresh]
//
// Mirrors the cache/fetch logic in fetch-frame.ts (small, intentional
// duplication - this script stays dependency-free/no-build-step, like the
// other scripts/*.mjs dev tools).

import { readFileSync, existsSync, mkdirSync, writeFileSync } from "node:fs";
import path from "node:path";

function loadEnvFile(envPath) {
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

const repoRoot = path.join(import.meta.dirname, "..");
loadEnvFile(path.join(repoRoot, ".env"));

const { FIGMA_ACCESS_TOKEN, FIGMA_FILE_KEY, FIGMA_NODE_ID } = process.env;
for (const [name, value] of Object.entries({ FIGMA_ACCESS_TOKEN, FIGMA_FILE_KEY, FIGMA_NODE_ID })) {
  if (!value) {
    console.error(`Missing ${name}. Copy .env.example to .env and fill it in.`);
    process.exit(1);
  }
}

const forceRefresh = process.argv.includes("--refresh");
const cachePath = path.join(
  repoRoot, ".cache", "figma", FIGMA_FILE_KEY, `${FIGMA_NODE_ID.replace(":", "-")}.json`,
);

if (!forceRefresh && existsSync(cachePath)) {
  console.log(`Cache hit: ${cachePath} (pass --refresh to re-fetch from Figma)`);
  process.exit(0);
}

const url = `https://api.figma.com/v1/files/${FIGMA_FILE_KEY}/nodes?ids=${FIGMA_NODE_ID}`;
const res = await fetch(url, { headers: { "X-Figma-Token": FIGMA_ACCESS_TOKEN } });

if (!res.ok) {
  console.error(`Figma API error: ${res.status} ${res.statusText}`);
  console.error(await res.text());
  process.exit(1);
}

const body = await res.json();
const document = body.nodes?.[FIGMA_NODE_ID]?.document;
if (!document) {
  console.error(`Node ${FIGMA_NODE_ID} not found in response. Keys present: ${Object.keys(body.nodes ?? {})}`);
  process.exit(1);
}

mkdirSync(path.dirname(cachePath), { recursive: true });
writeFileSync(cachePath, JSON.stringify(document, null, 2));
console.log(`Fetched "${document.name}" from Figma, wrote ${cachePath}`);
