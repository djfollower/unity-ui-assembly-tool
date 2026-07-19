#!/usr/bin/env node
// Regenerates src/generated/*.ts from schemas/*.schema.json.
// Run via `npm run generate` (packages/contracts/package.json).

import { compileFromFile } from "json-schema-to-typescript";
import { mkdir, writeFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import path from "node:path";

const here = path.dirname(fileURLToPath(import.meta.url));
const schemasDir = path.join(here, "..", "schemas");
const outDir = path.join(here, "..", "src", "generated");

const schemas = [
  "element-tree.schema.json",
  "catalog-entry.schema.json",
  "match-result.schema.json",
];

await mkdir(outDir, { recursive: true });

for (const schema of schemas) {
  const ts = await compileFromFile(path.join(schemasDir, schema), {
    cwd: schemasDir,
    bannerComment: "",
  });
  const outFile = path.join(outDir, schema.replace(".schema.json", ".ts"));
  await writeFile(outFile, ts);
  console.log(`wrote ${path.relative(process.cwd(), outFile)}`);
}
