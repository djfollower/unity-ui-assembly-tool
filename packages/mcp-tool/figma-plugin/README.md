# Frame export plugin (local dev only)

The free-tier Figma REST API quota is 6 requests/month - far too scarce to build a dev workflow
around. This plugin runs inside Figma itself (not network-metered) and exports the selected
frame's full node tree as JSON, in the same shape `fetch-frame.ts` would have cached from the
REST API - so no pipeline code needs to know which path the data came from.

## Install

Figma desktop app -> **Plugins -> Development -> Import plugin from manifest...** -> select
`packages/mcp-tool/figma-plugin/manifest.json`.

## Run

1. Open the target file, select exactly one frame/component on the canvas.
2. **Plugins -> Development -> UI Assembler Slice - Frame Export**.
3. Click **Download JSON** in the plugin panel.
4. Move the downloaded file to `.cache/figma/<fileKey>/<nodeId>.json` (the panel shows the exact
   path - `fetch-frame.ts` and `scripts/fetch-figma-frame.mjs` both read from there first, before
   ever touching the network).

Re-run whenever the frame's design changes in Figma. Everything downstream (parse-tree.ts,
reduce.ts, normalize.ts) is unaffected either way - they only ever see the cached JSON.
