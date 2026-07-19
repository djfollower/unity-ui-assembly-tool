#!/usr/bin/env node
// Compiles each schema with ajv and validates it against the illustrative
// example from the implementation plan, so schema drift fails loudly.

import Ajv from "ajv";
import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import path from "node:path";

const here = path.dirname(fileURLToPath(import.meta.url));
const schemasDir = path.join(here, "..", "schemas");

const ajv = new Ajv({ allErrors: true, strict: true });

const cases = [
  {
    schema: "element-tree.schema.json",
    example: {
      source: "figma",
      frame_id: "1:234",
      source_frame: { w: 1290, h: 2796 },
      canvas_reference: { w: 1080, h: 1920 },
      canvas_match_mode: "match_width_or_height",
      canvas_match_value: 0.5,
      elements: [
        {
          id: "btn_buy",
          figma_node_id: "4:81",
          type: "button",
          rect: { x: 420, y: 900, w: 240, h: 80 },
          visual_description: "red rounded button, label 'Buy'",
          text_content: "Buy",
          is_component_instance: true,
          children: [],
        },
      ],
    },
  },
  {
    schema: "catalog-entry.schema.json",
    example: [
      {
        id: "common__btn_red",
        path: "Assets/Common/Prefabs/RedButton.prefab",
        type: "prefab",
        feature: "Common",
        render: {
          image_type: "Sliced",
          border: [24, 24, 24, 24],
          ppu: 100,
          ppu_multiplier: 1.5,
          native_size: { w: 200, h: 80 },
          tint: "#C0392B",
        },
        thumbnail_path: "thumbnails/common__btn_red.png",
        role: "base",
        interactive: true,
        visual_description: "red rounded button",
      },
    ],
  },
  {
    schema: "match-result.schema.json",
    example: [
      {
        element_id: "btn_buy",
        status: "matched",
        matched_asset_id: "common__btn_red",
        signals: { visual: 0.91, structural: 0.88, agree: true, margin: 0.19 },
        resize: { safe: true, size_delta_to: { w: 240, h: 80 } },
      },
    ],
  },
];

let failed = false;

for (const { schema, example } of cases) {
  const schemaPath = path.join(schemasDir, schema);
  const schemaJson = JSON.parse(await readFile(schemaPath, "utf8"));
  const validate = ajv.compile(schemaJson);
  const valid = validate(example);
  if (valid) {
    console.log(`ok   ${schema}`);
  } else {
    failed = true;
    console.error(`FAIL ${schema}`);
    console.error(validate.errors);
  }
}

if (failed) {
  process.exit(1);
}
