// Local dev plugin: exports a hand-picked subset of the selected frame's
// node tree as JSON, matching the shape fetch-frame.ts caches from the REST
// API (see packages/mcp-tool/src/figma/fetch-frame.ts's FigmaNode type) plus
// three new annotation fields (selected/typeTag/compositeGroupId - see §5 of
// the plan this shipped from) a human adds via the checkbox tree UI in
// ui.html. Exists because the REST API's free-tier quota (6 requests/month)
// is far too scarce to build a dev workflow around - the Plugin API runs
// inside Figma itself and isn't network-metered at all.
//
// Selection/type-tagging/composite-marking replaces reduce.ts's LLM
// judgment call for deciding which layers are real UI elements - the human
// does it here, live against the real rendered mockup, no Figma layer-
// naming discipline required.
//
// Install: Figma desktop app -> Plugins -> Development -> Import plugin
// from manifest... -> select this folder's manifest.json.
// Run: select exactly one frame/component on the canvas, then run the
// plugin (Plugins -> Development -> UI Assembler Slice - Frame Export).

figma.showUI(__html__, { width: 480, height: 640 });

const COMPOSITE_GROUP_KEY = "uas:compositeGroupId";

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

  // Durable composite-group marker, written by confirmCombine (see below)
  // and persisted in the Figma file itself via setPluginData - read back
  // here so a group marked on a previous plugin run shows correctly again
  // without any extra async lookups.
  if ("getPluginData" in node) {
    const groupId = node.getPluginData(COMPOSITE_GROUP_KEY);
    if (groupId) result.compositeGroupId = groupId;
  }

  if ("children" in node && node.children) {
    result.children = node.children.map(serializeNode);
  }

  return result;
}

function generateGroupId() {
  return `cg_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 8)}`;
}

async function handleRequestThumbnails(nodeIds) {
  for (const id of nodeIds) {
    let target = null;
    try {
      target = await figma.getNodeByIdAsync(id);
    } catch (err) {
      figma.ui.postMessage({ type: "thumbnailError", nodeId: id, message: String((err && err.message) || err) });
      continue;
    }

    if (!target || !("exportAsync" in target)) {
      figma.ui.postMessage({ type: "thumbnailError", nodeId: id, message: "unavailable" });
      continue;
    }

    try {
      // Fixed pixel WIDTH, not SCALE - SCALE is relative to the node's own
      // size and would produce huge exports for large frames / illegibly
      // tiny ones for small icons. A fixed width keeps every thumbnail
      // cheap regardless of what kind of node it is.
      const bytes = await target.exportAsync({ format: "PNG", constraint: { type: "WIDTH", value: 64 } });
      const dataUri = `data:image/png;base64,${figma.base64Encode(bytes)}`;
      figma.ui.postMessage({ type: "thumbnail", nodeId: id, dataUri });
    } catch (err) {
      figma.ui.postMessage({ type: "thumbnailError", nodeId: id, message: String((err && err.message) || err) });
    }
  }
}

async function handleConfirmCombine(nodeIds) {
  const groupId = generateGroupId();
  const confirmedIds = [];
  for (const id of nodeIds) {
    const node = await figma.getNodeByIdAsync(id);
    if (!node || !("setPluginData" in node)) continue;
    node.setPluginData(COMPOSITE_GROUP_KEY, groupId);
    confirmedIds.push(id);
  }
  figma.ui.postMessage({ type: "combineConfirmed", groupId, nodeIds: confirmedIds });
}

async function handleUncombine(groupId, nodeIds) {
  const confirmedIds = [];
  for (const id of nodeIds) {
    const node = await figma.getNodeByIdAsync(id);
    if (!node || !("setPluginData" in node)) continue;
    // Setting plugin data to "" is the documented delete idiom - there's no
    // separate "delete this key" method.
    node.setPluginData(COMPOSITE_GROUP_KEY, "");
    confirmedIds.push(id);
  }
  figma.ui.postMessage({ type: "uncombineConfirmed", groupId, nodeIds: confirmedIds });
}

figma.ui.onmessage = async (msg) => {
  if (!msg || typeof msg.type !== "string") return;

  if (msg.type === "requestThumbnails") {
    await handleRequestThumbnails(msg.nodeIds || []);
  } else if (msg.type === "confirmCombine") {
    await handleConfirmCombine(msg.nodeIds || []);
  } else if (msg.type === "uncombine") {
    await handleUncombine(msg.groupId, msg.nodeIds || []);
  }
};

const selection = figma.currentPage.selection;
if (selection.length !== 1) {
  figma.ui.postMessage({
    type: "error",
    message: `Select exactly one frame/component on the canvas, then re-run this plugin. (Currently selected: ${selection.length})`,
  });
} else {
  const node = selection[0];
  const tree = serializeNode(node);
  figma.ui.postMessage({
    type: "init",
    tree,
    fileKey: figma.fileKey || "unknown-file-key",
    nodeId: node.id,
    nodeName: node.name,
  });
}
