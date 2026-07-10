// T1.9: LLM reduction pass. Collapses the intermediate node tree to logical
// elements, using layer names and instance refs as hints. Outputs candidate
// elements (pre-normalization - rects are still frame-relative pixels from
// parse-tree.ts; normalize.ts (T1.10) converts to canvas reference space).
// Feeds Gate 1 directly.
//
// Shells out to the `claude` CLI rather than @anthropic-ai/sdk, reusing this
// environment's already-authenticated session instead of requiring a
// separate ANTHROPIC_API_KEY. Deliberately does NOT use --bare: bare mode
// only accepts ANTHROPIC_API_KEY/apiKeyHelper auth (never OAuth/keychain),
// which would defeat the point. Dev-time choice - swap for the SDK + a real
// API key before this needs to run unattended in CI (the CLI needs an
// interactive login).

import { spawn } from "node:child_process";
import type { ElementTree } from "@ui-assembler-slice/contracts";
import type { IntermediateNode } from "./parse-tree.js";

export type ReducedElement = ElementTree["elements"][number];

const INSTRUCTIONS = `You are reducing a raw Figma layer tree into a logical list of UI elements for a Unity UI-assembly pipeline.

Rules:
- A "real UI element" is something a developer would build as a distinct Image, TextMeshPro, or prefab instance: buttons, icons, labels, panels, overlays/scrims. Purely decorative or structural nodes (guides, masks, backgrounds duplicated from a HUD context behind a popup, grouping frames with no visual identity of their own, alternate/hidden state variants) are NOT elements.
- When in doubt, exclude. Precision matters as much as recall.
- Use layer names as strong hints for "id" and "type" - if a layer is already named like "button_x" or "text_title", trust that naming. Preserve the layer's exact original casing in "id" - do not lowercase or otherwise normalize it.
- "is_component_instance" should be true only for nodes with figmaType "INSTANCE".
- "text_content" should only be set for nodes that are themselves text or whose primary content is a text child.
- Do not include nodes with visible: false, or nodes nested inside a visible:false ancestor.
- Output ONLY a JSON array of elements, no markdown fences, no prose before or after. Each element:
  { "id": string, "figma_node_id": string, "type": string, "rect": {"x":number,"y":number,"w":number,"h":number}, "visual_description": string, "text_content"?: string, "is_component_instance": boolean, "children": [] }
- Keep "children" empty for each element unless a node is genuinely a compound template that must carry structured children - for most simple UI screens, flat is correct.`;

export async function reduce(intermediateTree: IntermediateNode): Promise<ReducedElement[]> {
  const prompt = `${INSTRUCTIONS}\n\nRaw intermediate node tree:\n${JSON.stringify(intermediateTree, null, 2)}`;
  const cliOutput = await runClaudeCli(prompt);
  const jsonText = extractJson(cliOutput);

  try {
    return JSON.parse(jsonText) as ReducedElement[];
  } catch (err) {
    throw new Error(`reduce: failed to parse LLM output as JSON: ${(err as Error).message}\n---\n${jsonText}`);
  }
}

function runClaudeCli(prompt: string): Promise<string> {
  return new Promise((resolve, reject) => {
    const child = spawn("claude", ["-p", "--output-format", "json"], { stdio: ["pipe", "pipe", "pipe"] });

    let stdout = "";
    let stderr = "";
    child.stdout.on("data", (chunk) => { stdout += chunk; });
    child.stderr.on("data", (chunk) => { stderr += chunk; });

    child.on("error", reject);
    child.on("close", (code) => {
      if (code !== 0) {
        reject(new Error(`claude CLI exited with code ${code}: ${stderr}`));
        return;
      }
      resolve(stdout);
    });

    child.stdin.write(prompt);
    child.stdin.end();
  });
}

function extractJson(cliOutput: string): string {
  let text = cliOutput;
  try {
    const envelope = JSON.parse(cliOutput) as { result?: string };
    if (typeof envelope.result === "string") {
      text = envelope.result;
    }
  } catch {
    // cliOutput wasn't the JSON envelope (e.g. --output-format text) - use as-is.
  }

  const fenced = text.match(/```(?:json)?\s*([\s\S]*?)```/);
  return (fenced ? fenced[1] : text).trim();
}
