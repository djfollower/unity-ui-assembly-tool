// Shells out to the `claude` CLI, reusing this environment's already-
// authenticated session (subscription/OAuth login) instead of a separate
// ANTHROPIC_API_KEY. Originally T1.9's (reduce.ts) approach; generalized
// here so visual-signal.ts (T2.3) can use the same mechanism for its
// multimodal calls - both were paying for the same thing twice before this.
//
// Deliberately does NOT use --bare: bare mode only accepts
// ANTHROPIC_API_KEY/apiKeyHelper auth (never OAuth/keychain), which would
// defeat the point.
//
// Images are passed as file paths, not inline base64 blocks: the CLI reads
// local files itself via its own tools when a prompt references a path -
// confirmed by manually running `claude -p` against two real PNGs and
// getting a correct multi-turn comparison back (see HANDOFF.md's T2.3
// notes for the measured cost/latency of that path before this adapter
// existed - it's still real, just no longer a metered API cost on top of
// an existing subscription).
//
// A nested `claude -p` doesn't automatically get read access to arbitrary
// paths on disk (confirmed the hard way: a first version of this wrote
// temp images to node:os's tmpdir() and the nested CLI refused to read
// them - "I don't have permission to read those temp files"). `--add-dir`
// explicitly grants the session access to a directory; complete() passes
// each image's parent directory.

import { spawn } from "node:child_process";
import path from "node:path";
import type { AgentAdapter } from "./adapter.js";

function runClaudeCli(prompt: string, allowedDirs: string[]): Promise<string> {
  return new Promise((resolve, reject) => {
    const args = ["-p", "--output-format", "json"];
    if (allowedDirs.length > 0) {
      args.push("--add-dir", ...allowedDirs);
    }
    const child = spawn("claude", args, { stdio: ["pipe", "pipe", "pipe"] });

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

function extractResult(cliOutput: string): string {
  try {
    const envelope = JSON.parse(cliOutput) as { result?: string };
    if (typeof envelope.result === "string") {
      return envelope.result;
    }
  } catch {
    // cliOutput wasn't the JSON envelope (e.g. --output-format text) - use as-is.
  }
  return cliOutput;
}

function probeAvailability(): Promise<boolean> {
  return new Promise((resolve) => {
    const probe = spawn("claude", ["--version"], { stdio: "ignore" });
    probe.on("error", () => resolve(false));
    probe.on("close", (code) => resolve(code === 0));
  });
}

export const claudeCliAdapter: AgentAdapter = {
  name: "claude",
  isAvailable: probeAvailability,
  async complete(prompt, imagePaths = []) {
    const fullPrompt = imagePaths.length === 0
      ? prompt
      : `${prompt}\n\nRead each of these local image files before answering:\n${imagePaths.map((p, i) => `${i + 1}. ${p}`).join("\n")}`;
    const allowedDirs = [...new Set(imagePaths.map((p) => path.dirname(p)))];
    const cliOutput = await runClaudeCli(fullPrompt, allowedDirs);
    return extractResult(cliOutput);
  },
};
