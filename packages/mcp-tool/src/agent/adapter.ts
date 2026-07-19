// Agent-agnostic LLM adapter. Both reduce.ts (T1.9) and visual-signal.ts
// (T2.3) need an LLM call (text-only and multimodal respectively) - this
// lets either one run against whatever coding-agent CLI the user already
// has installed and authenticated, instead of hardcoding one provider or
// requiring a separate metered API key.
//
// Claude is the only implemented adapter (see claude-cli-adapter.ts) -
// verified working against this repo's real fixtures. Unity AI Assistant
// (Unity.AI.Assistant.Editor.Api, RunHeadless + AttachedContext.AddImageContent)
// was considered as a second one - it looks technically feasible from
// Unity's docs (async, no Assistant window required, takes a Texture
// directly for vision input) - but wasn't implemented: no license available
// to test against, and it would be a genuinely different shape of adapter
// (runs inside the Unity Editor process, not a Node-spawned CLI), not just
// another entry in this list. See HANDOFF.md.
//
// Add a new adapter by implementing this interface and registering it in
// claude-cli-adapter.ts's neighbor - a getAgentAdapter() priority list.

export interface AgentAdapter {
  readonly name: string;
  isAvailable(): Promise<boolean>;
  // imagePaths: absolute paths to local image files the agent should read
  // as part of answering the prompt. All adapters here are expected to
  // shell out to a CLI that has its own file-reading tools, rather than
  // inlining base64 image blocks into a raw API request - that's what lets
  // this ride on the CLI's existing login instead of a separate API key.
  complete(prompt: string, imagePaths?: string[]): Promise<string>;
}
