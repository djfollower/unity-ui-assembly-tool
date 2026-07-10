// Local dev plugin: exports the selected frame's full node tree as JSON,
// matching the shape fetch-frame.ts caches from the REST API (see
// packages/mcp-tool/src/figma/fetch-frame.ts's FigmaNode type). Exists
// because the REST API's free-tier quota (6 requests/month) is far too
// scarce to build a dev workflow around - the Plugin API runs inside Figma
// itself and isn't network-metered at all.
//
// Install: Figma desktop app -> Plugins -> Development -> Import plugin
// from manifest... -> select this folder's manifest.json.
// Run: select exactly one frame/component on the canvas, then run the
// plugin (Plugins -> Development -> UI Assembler Slice - Frame Export).

figma.showUI(__html__, { width: 420, height: 220 });

function serializeNode(node) {
  const result = {
    id: node.id,
    name: node.name,
    type: node.type,
    visible: node.visible !== false,
  };

  if ("absoluteBoundingBox" in node && node.absoluteBoundingBox) {
    result.absoluteBoundingBox = {
      x: node.absoluteBoundingBox.x,
      y: node.absoluteBoundingBox.y,
      width: node.absoluteBoundingBox.width,
      height: node.absoluteBoundingBox.height,
    };
  }

  if (node.type === "TEXT" && "characters" in node) {
    result.characters = node.characters;
  }

  if ("effects" in node && node.effects && node.effects.length > 0) {
    result.effects = node.effects.map((e) => ({ type: e.type, visible: e.visible !== false }));
  }

  if ("children" in node && node.children) {
    result.children = node.children.map(serializeNode);
  }

  return result;
}

const selection = figma.currentPage.selection;
if (selection.length !== 1) {
  figma.ui.postMessage({
    type: "error",
    message: `Select exactly one frame/component on the canvas, then re-run this plugin. (Currently selected: ${selection.length})`,
  });
} else {
  const node = selection[0];
  const data = serializeNode(node);
  figma.ui.postMessage({
    type: "export",
    json: JSON.stringify(data, null, 2),
    fileKey: figma.fileKey || "unknown-file-key",
    nodeId: node.id,
    nodeName: node.name,
  });
}
