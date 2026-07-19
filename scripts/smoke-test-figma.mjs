#!/usr/bin/env node
// T1.2: verifies Figma REST access to the chosen frame before any real
// parsing logic is written. Reads FIGMA_ACCESS_TOKEN / FIGMA_FILE_KEY /
// FIGMA_NODE_ID from .env (see .env.example) and prints the node count.

import { readFileSync, existsSync } from "node:fs";
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

loadEnvFile(path.join(import.meta.dirname, "..", ".env"));

const { FIGMA_ACCESS_TOKEN, FIGMA_FILE_KEY, FIGMA_NODE_ID } = process.env;

for (const [name, value] of Object.entries({ FIGMA_ACCESS_TOKEN, FIGMA_FILE_KEY, FIGMA_NODE_ID })) {
  if (!value) {
    console.error(`Missing ${name}. Copy .env.example to .env and fill it in.`);
    process.exit(1);
  }
}

function countNodes(node) {
  if (!node || typeof node !== "object") return 0;
  let count = 1;
  for (const child of node.children ?? []) count += countNodes(child);
  return count;
}

const url = `https://api.figma.com/v1/files/${FIGMA_FILE_KEY}/nodes?ids=${FIGMA_NODE_ID}`;
const res = await fetch(url, { headers: { "X-Figma-Token": FIGMA_ACCESS_TOKEN } });

if (!res.ok) {
  console.error(`Figma API error: ${res.status} ${res.statusText}`);
  console.error(await res.text());
  process.exit(1);
}

const body = await res.json();
const entry = body.nodes?.[FIGMA_NODE_ID];

if (!entry) {
  console.error(`Node ${FIGMA_NODE_ID} not found in response. Keys present: ${Object.keys(body.nodes ?? {})}`);
  process.exit(1);
}

const nodeCount = countNodes(entry.document);
console.log(`OK: fetched node ${FIGMA_NODE_ID} ("${entry.document.name}") from file ${FIGMA_FILE_KEY}`);
console.log(`Total node count (including root): ${nodeCount}`);
