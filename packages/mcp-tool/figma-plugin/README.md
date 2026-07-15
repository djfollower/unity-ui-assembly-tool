# Frame export plugin (local dev only)

The free-tier Figma REST API quota is 6 requests/month - far too scarce to build a dev workflow
around. This plugin runs inside Figma itself (not network-metered) and exports a hand-picked
subset of the selected frame's node tree as JSON, in the same shape `fetch-frame.ts` would have
cached from the REST API plus a few extra annotation fields (see "What you're picking" below) - so
no pipeline code needs to know which path the data came from.

Selecting which nodes matter, tagging their type, and marking layered composites all happen here,
live against the real rendered mockup - no Figma layer-naming discipline required, and no need to
round-trip back into Figma just to rename a layer the reduction step got wrong.

## Install

Figma desktop app -> **Plugins -> Development -> Import plugin from manifest...** -> select
`packages/mcp-tool/figma-plugin/manifest.json`. There's no build step - `code.js`/`ui.html` are
plain hand-written JS/HTML, edited directly. Re-import after every edit to pick up changes.

## Run

1. Open the target file, select exactly one frame/component on the canvas.
2. **Plugins -> Development -> UI Assembler Slice - Frame Export**. The plugin panel loads a
   checkbox tree of the frame's contents - only the top level is expanded by default (a real frame
   can be hundreds of nodes deep; everything below the top level stays collapsed until you open it).

### What you're picking

- **Check** the nodes that should become UI elements in the assembled prefab. Unchecked nodes
  (decorative layers, structural groups, alternate states, etc.) are simply skipped downstream -
  nothing needs to be deleted or hidden in the actual Figma file.
- Checking a node reveals a **type tag** (button / icon / panel / image / other) - pick whichever
  looks right against the thumbnail. Text layers are tagged automatically, no picker needed.
- If several layers are really one visual (e.g. a button's frame + backing + icon glyph, stacked
  and rendered as separate Unity sprites), select them with **Combine**: click it, click each node
  to add it to the pending set, then **Confirm combine**. This is saved directly on the Figma nodes
  (via plugin data), so it persists across re-runs of the plugin - re-running after a design change
  still remembers which layers were marked as one composite. Click a "combined" badge to undo it.
- Thumbnails load lazily as you expand a node's children - expect a brief delay the first time you
  open a large subtree.

## Export

1. Click **Download JSON** in the plugin panel once you're happy with your selections.
2. Move the downloaded file to `.cache/figma/<fileKey>/<nodeId>.json` (the panel shows the exact
   path - `fetch-frame.ts` and `scripts/fetch-figma-frame.mjs` both read from there first, before
   ever touching the network).

The exported JSON still contains the frame's *entire* tree (nothing is pruned), annotated with
`selected`/`typeTag`/`compositeGroupId` on the nodes you picked - deciding how to turn that into a
real `element-tree.json` is a separate, Node-side step. Re-run whenever the frame's design changes
in Figma; `parse-tree.ts`/`normalize.ts` are unaffected either way, they only ever see the cached
JSON.
