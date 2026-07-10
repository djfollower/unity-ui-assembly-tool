#!/usr/bin/env node
// Entry point for the MCP tool side of the pipeline. No MCP server wrapper
// yet for the slice — this is invoked directly (see scripts/ in repo root).
// Subcommands land as each week's tasks complete:
//   fetch-frame | reduce | build-catalog-descriptions | match

const [, , command] = process.argv;

switch (command) {
  default:
    console.error(`Unknown or unimplemented command: ${command ?? "(none)"}`);
    process.exit(1);
}
