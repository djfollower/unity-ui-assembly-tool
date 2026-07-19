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

  await captureCombinedHiRes(groupId, confirmedIds);
}

// True when `potentialAncestor` is a real Figma ancestor of `node` (walking
// up via .parent) - same anchor concept reduce-from-selection.ts detects
// independently on the exported JSON tree, but needed here too since this
// runs against live Figma nodes before any export happens.
function isAncestor(potentialAncestor, node) {
  let current = node.parent;
  while (current) {
    if (current.id === potentialAncestor.id) return true;
    current = current.parent;
  }
  return false;
}

// A combine group's real anchor, if it has one - a member that's the Figma
// parent (direct or indirect) of the others, e.g. button_x's parent
// instance containing its frame/base/icon as children. Returns null for a
// pure-sibling combine with no shared parent among its own members -
// expected to be rare (see reduce-from-selection.ts's own comment on this).
async function findAnchor(nodeIds) {
  const nodes = [];
  for (const id of nodeIds) {
    const n = await figma.getNodeByIdAsync(id);
    if (n) nodes.push(n);
  }
  for (const candidate of nodes) {
    const isAncestorOfAnother = nodes.some((other) => other !== candidate && isAncestor(candidate, other));
    if (isAncestorOfAnother) return candidate;
  }
  return null;
}

// Real-resolution capture of a Combine group, fired once at confirm time
// (not lazily/eagerly the way the 64px thumbnails are) - the source image a
// human can later import as a real new catalog asset in Stage 3 review if
// the matcher can't find an existing one that scores well (see
// reduce-from-selection.ts's fallbackCaptures / match.ts's
// fallback_eligible status). Only supports groups with a real anchor - a
// pure-sibling combine with no shared parent has no single exportable node
// representing "the whole group," and is explicitly out of scope for this
// pass: logged, not fatal, that group still gets composite: true normally,
// just with no fallback capture available (same as today's plain "missing"
// behavior, not a regression).
async function captureCombinedHiRes(groupId, nodeIds) {
  const anchor = await findAnchor(nodeIds);
  if (!anchor) {
    console.log(`captureCombinedHiRes: group ${groupId} has no anchor - hi-res capture skipped`);
    return;
  }
  if (!("exportAsync" in anchor)) {
    figma.ui.postMessage({ type: "combinedExportError", groupId, message: "anchor not exportable" });
    return;
  }

  // Text stays a live TMPro object downstream, not baked pixels - hide any
  // TEXT descendant of the anchor before exporting, restore regardless of
  // outcome. No existing helper for this in the codebase before now.
  const textDescendants = "findAll" in anchor ? anchor.findAll((n) => n.type === "TEXT" && n.visible !== false) : [];
  const originalVisibility = textDescendants.map((n) => n.visible);
  try {
    for (const n of textDescendants) n.visible = false;
    // No WIDTH/SCALE constraint, unlike the 64px thumbnail path in
    // handleRequestThumbnails - real resolution is the whole point here.
    const bytes = await anchor.exportAsync({ format: "PNG" });
    const dataUri = `data:image/png;base64,${figma.base64Encode(bytes)}`;
    figma.ui.postMessage({ type: "combinedExport", groupId, dataUri });
  } catch (err) {
    figma.ui.postMessage({ type: "combinedExportError", groupId, message: String((err && err.message) || err) });
  } finally {
    textDescendants.forEach((n, i) => {
      n.visible = originalVisibility[i];
    });
  }
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

// Existing composite groups (compositeGroupId persisted via
// setPluginData from a PRIOR plugin session, read back in serializeNode
// above) don't carry their hi-res capture forward - combinedHiResExport
// only ever lived in ui.html's in-memory combinedExports Map (see
// handleConfirmCombine/captureCombinedHiRes), which is gone once the
// plugin closes. Re-running captureCombinedHiRes for every pre-existing
// group on load (not just freshly-confirmed ones) keeps re-opening the
// plugin on an already-combined frame from silently losing the
// visual-signal ground truth for those elements downstream - confirmed
// as the cause of a real "no ground-truth image available" matcher
// failure, not a theoretical gap.
function collectExistingGroups(node, into) {
  if (node.compositeGroupId) {
    const members = into.get(node.compositeGroupId) || [];
    members.push(node.id);
    into.set(node.compositeGroupId, members);
  }
  (node.children || []).forEach((child) => collectExistingGroups(child, into));
}

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

  const existingGroups = new Map();
  collectExistingGroups(tree, existingGroups);
  for (const [groupId, nodeIds] of existingGroups) {
    captureCombinedHiRes(groupId, nodeIds);
  }
}
