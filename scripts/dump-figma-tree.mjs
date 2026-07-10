#!/usr/bin/env node
// Reference helper for T1.3: dumps the chosen frame's raw node hierarchy
// (name, type, visible, id) as indented text, for the human doing the
// golden-elements.json labeling to cross-check against the Figma layer panel.
// This is NOT a substitute for looking at the actual frame - see the
// annotation guidance in the implementation plan.

import { readFileSync, existsSync, writeFileSync } from "node:fs";
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

const url = `https://api.figma.com/v1/files/${FIGMA_FILE_KEY}/nodes?ids=${FIGMA_NODE_ID}`;
const res = await fetch(url, { headers: { "X-Figma-Token": FIGMA_ACCESS_TOKEN } });
const body = await res.json();
const doc = body.nodes[FIGMA_NODE_ID].document;

const lines = [];
function walk(node, depth) {
  const vis = node.visible === false ? " [HIDDEN]" : "";
  const box = node.absoluteBoundingBox;
  const size = box ? ` ${Math.round(box.width)}x${Math.round(box.height)}` : "";
  lines.push(`${"  ".repeat(depth)}- ${node.name} (${node.type}${size})${vis} [id:${node.id}]`);
  for (const child of node.children ?? []) walk(child, depth + 1);
}
walk(doc, 0);

const outPath = path.join(import.meta.dirname, "..", "fixtures", "figma-tree-reference.txt");
writeFileSync(outPath, lines.join("\n") + "\n");
console.log(`Wrote ${lines.length} nodes to ${outPath}`);
