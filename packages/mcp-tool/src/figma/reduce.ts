// T1.9: LLM reduction pass. Collapses the intermediate node tree to logical
// elements, using layer names and instance refs as hints. Outputs candidate
// elements (pre-normalization - rects are still frame-relative pixels from
// parse-tree.ts; normalize.ts (T1.10) converts to canvas reference space).
// Feeds Gate 1 directly.
//
// Uses the agent adapter (src/agent/ - originally this file's own private
// CLI-shelling logic, generalized when visual-signal.ts needed the same
// "reuse an already-authenticated agent CLI, not a separate metered API
// key" approach for its multimodal calls too). Still needs the CLI's
// interactive login, so this can't run fully unattended in CI yet - that
// part is unchanged from before this refactor, just now shared instead of
// duplicated.

import type { ElementTree } from "@ui-assembler-slice/contracts";
import { getAgentAdapter } from "../agent/registry.js";
import { parseJsonResponse } from "../agent/parse-json-response.js";
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
  const adapter = await getAgentAdapter();
  const responseText = await adapter.complete(prompt);
  return parseJsonResponse<ReducedElement[]>(responseText);
}
