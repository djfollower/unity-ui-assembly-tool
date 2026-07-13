// Picks the first available adapter from a priority-ordered list. One entry
// today (see adapter.ts's header comment on why Unity AI Assistant isn't
// in this list yet) - adding a second means importing it here and adding
// it to ADAPTERS, nothing else needs to change.

import type { AgentAdapter } from "./adapter.js";
import { claudeCliAdapter } from "./claude-cli-adapter.js";

const ADAPTERS: AgentAdapter[] = [claudeCliAdapter];

export async function getAgentAdapter(preferredName?: string): Promise<AgentAdapter> {
  const candidates = preferredName ? ADAPTERS.filter((a) => a.name === preferredName) : ADAPTERS;

  for (const adapter of candidates) {
    if (await adapter.isAvailable()) {
      return adapter;
    }
  }

  const tried = candidates.map((a) => a.name).join(", ") || "none registered";
  throw new Error(
    `getAgentAdapter: no available agent CLI found (tried: ${tried}). ` +
    "Install/authenticate the Claude CLI (`claude --version` should succeed), " +
    "or add a new adapter under src/agent/ and register it in registry.ts.",
  );
}
