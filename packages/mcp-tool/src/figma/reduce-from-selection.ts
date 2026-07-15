// Deterministic, non-LLM alternative to reduce.ts's reduce() - consumes an
// IntermediateNode tree already annotated with selected/typeTag/
// compositeGroupId (the figma-plugin/ checkbox tree's export - see its
// README), producing the same ReducedElement[] shape reduce.ts's LLM call
// would, pre-normalization (feeds straight into normalize.ts, unchanged).
// No naming discipline, no LLM cost - a human already made every real/
// decorative/type/composite judgment call by clicking in the plugin; this
// just turns that into the schema shape.

import type { ElementTree } from "@ui-assembler-slice/contracts";
import type { IntermediateNode } from "./parse-tree.js";

export type ReducedElement = ElementTree["elements"][number];
type Rect = ReducedElement["rect"];

interface GroupMember {
  node: IntermediateNode;
  // ids of every ancestor of `node`, root down to (not including) itself -
  // how partitionGroup below tells an "anchor" (a member that's the real
  // Figma parent of other members) from a true independent leaf member.
  ancestorIds: Set<string>;
}

// Phase 1: one full walk from the frame root, collecting every node
// carrying a compositeGroupId anywhere in the tree - deliberately NOT
// scoped to direct siblings. A real combine group can have its "anchor"
// member be the actual Figma PARENT of its other members (confirmed
// against a real export: button_continue_main carries the same
// compositeGroupId as its own 7 children) - a sibling-only grouping pass
// would silently drop those 7.
function collectGroups(root: IntermediateNode): Map<string, GroupMember[]> {
  const groups = new Map<string, GroupMember[]>();

  function walk(node: IntermediateNode, ancestorIds: Set<string>): void {
    if (node.visible === false) return;

    if (node.compositeGroupId) {
      const members = groups.get(node.compositeGroupId) ?? [];
      members.push({ node, ancestorIds });
      groups.set(node.compositeGroupId, members);
    }

    const childAncestorIds = new Set(ancestorIds);
    childAncestorIds.add(node.id);
    for (const child of node.children) {
      walk(child, childAncestorIds);
    }
  }

  for (const child of root.children) {
    walk(child, new Set());
  }

  return groups;
}

interface GroupPartition {
  // The member that's an ancestor of at least one other member of the same
  // group, if any - a Figma GROUP/FRAME has no independent pixels beyond
  // its own children, so if it's combined alongside its own descendants it
  // isn't an extra visual layer, it's the group's identity/rect source
  // instead. Expected at most one in well-formed data (the plugin's
  // Combine action doesn't prevent pathological picks, but this only ever
  // reads whichever member happens to have descendants among the others).
  anchor: IntermediateNode | null;
  // Members that are NOT an ancestor of any other member - these are what
  // actually gets individually matched (compositeVisualSignals-style).
  leaves: IntermediateNode[];
}

function partitionGroup(members: GroupMember[]): GroupPartition {
  let anchor: IntermediateNode | null = null;
  const leaves: IntermediateNode[] = [];

  for (const member of members) {
    const isAncestorOfAnother = members.some(
      (other) => other !== member && other.ancestorIds.has(member.node.id),
    );
    if (isAncestorOfAnother) {
      anchor = member.node;
    } else {
      leaves.push(member.node);
    }
  }

  return { anchor, leaves };
}

function unionRect(rects: Rect[]): Rect {
  const minX = Math.min(...rects.map((r) => r.x));
  const minY = Math.min(...rects.map((r) => r.y));
  const maxX = Math.max(...rects.map((r) => r.x + r.w));
  const maxY = Math.max(...rects.map((r) => r.y + r.h));
  return { x: minX, y: minY, w: maxX - minX, h: maxY - minY };
}

function elementType(node: IntermediateNode): string {
  if (node.figmaType === "TEXT") return "text";
  return node.typeTag ?? "other";
}

function describeElement(node: IntermediateNode): string {
  return `${elementType(node)} - ${node.name}`;
}

export function reduceFromSelection(root: IntermediateNode): ReducedElement[] {
  const groups = collectGroups(root);
  const partitions = new Map<string, GroupPartition>();
  // Node id -> the groupId it belongs to, ONLY for the member Phase 2
  // should emit the composite wrapper at (the anchor, or - if a group
  // happens to have no anchor, a pure-sibling combine - the first leaf in
  // document order, as a stand-in position).
  const emissionPointId = new Map<string, string>();
  // Every member of every group (anchor + leaves) - Phase 2 skips these
  // entirely when they're not the emission point, since they're already
  // accounted for via the stored node references in `partitions`, not by
  // re-discovering them positionally in the tree.
  const consumedIds = new Set<string>();

  for (const [groupId, members] of groups) {
    const partition = partitionGroup(members);
    partitions.set(groupId, partition);
    for (const member of members) consumedIds.add(member.node.id);
    emissionPointId.set((partition.anchor ?? partition.leaves[0]).id, groupId);
  }

  const usedIds = new Set<string>();
  function uniqueId(name: string, disambiguator: string): string {
    const base = name.trim() || disambiguator;
    if (!usedIds.has(base)) {
      usedIds.add(base);
      return base;
    }
    // Deterministic, not an arbitrary counter - re-running produces the
    // same disambiguated id every time. Hit for real: the actual export
    // has a layer literally named "button_x" nested inside another node
    // also named "button_x" - naive id-from-name alone would collide and
    // silently corrupt match-result.json/NodeBuilder's id-keyed lookups.
    const disambiguated = `${base}__${disambiguator.replace(/[:;]/g, "-")}`;
    usedIds.add(disambiguated);
    return disambiguated;
  }

  function buildLeaf(node: IntermediateNode): ReducedElement {
    return {
      id: uniqueId(node.name, node.id),
      figma_node_id: node.id,
      type: elementType(node),
      rect: node.rect,
      visual_description: describeElement(node),
      text_content: node.figmaType === "TEXT" ? node.textContent : undefined,
      is_component_instance: node.isComponentInstance,
      children: [],
    };
  }

  function buildCompositeElement(groupId: string): ReducedElement {
    const partition = partitions.get(groupId);
    if (!partition) throw new Error(`reduceFromSelection: unknown group "${groupId}"`);
    const { anchor, leaves } = partition;
    const leafElements = leaves.map(buildLeaf);

    if (!anchor) {
      // No anchor - a pure-sibling combine with no shared parent among its
      // members. Synthesize an identity instead of borrowing one member's.
      return {
        id: uniqueId(`combined_${groupId}`, groupId),
        figma_node_id: `combine:${groupId}`,
        type: "other",
        rect: unionRect(leaves.map((l) => l.rect)),
        visual_description: `combined layers (${leaves.length})`,
        is_component_instance: false,
        composite: true,
        children: leafElements,
      };
    }

    // The anchor can ALSO have other children that were never part of the
    // combine - e.g. a text label sitting alongside shape layers the user
    // deliberately left out of Combine (the "hide the text, only combine
    // the shapes" workflow). Found for real: button_continue_main's own
    // "Currency + Text + CTA" child is selected but not a group member -
    // an earlier version of this function silently dropped it just
    // because its parent happened to be a composite anchor. Recursing into
    // the anchor's non-member children (reusing processChildren directly)
    // is what surfaces it correctly instead.
    const extraChildren = processChildren(anchor.children.filter((c) => !consumedIds.has(c.id)));

    if (extraChildren.length === 0) {
      return {
        id: uniqueId(anchor.name, anchor.id),
        figma_node_id: anchor.id,
        type: elementType(anchor),
        rect: anchor.rect,
        visual_description: describeElement(anchor),
        is_component_instance: anchor.isComponentInstance,
        composite: true,
        children: leafElements,
      };
    }

    // Anchor has real content beyond its combine group - it becomes a real
    // container instead, holding a synthesized composite sub-element (the
    // true combined shapes, sized to their own union bounds, not the
    // anchor's full rect which also spans the extra content) alongside
    // whatever else was found under it.
    const combinedSubElement: ReducedElement = {
      id: uniqueId(`${anchor.name}_combined`, groupId),
      figma_node_id: `combine:${groupId}`,
      type: elementType(anchor),
      rect: unionRect(leaves.map((l) => l.rect)),
      visual_description: describeElement(anchor),
      is_component_instance: anchor.isComponentInstance,
      composite: true,
      children: leafElements,
    };

    return {
      id: uniqueId(anchor.name, anchor.id),
      figma_node_id: anchor.id,
      type: elementType(anchor),
      rect: anchor.rect,
      visual_description: describeElement(anchor),
      is_component_instance: anchor.isComponentInstance,
      container: true,
      children: [combinedSubElement, ...extraChildren],
    };
  }

  function processChildren(children: IntermediateNode[]): ReducedElement[] {
    const result: ReducedElement[] = [];

    for (const child of children) {
      if (child.visible === false) continue;

      const groupId = emissionPointId.get(child.id);
      if (groupId) {
        result.push(buildCompositeElement(groupId));
        continue;
      }
      if (consumedIds.has(child.id)) {
        // A non-emission-point member of some group - already represented
        // by that group's wrapper (built from stored node references, not
        // re-discovered here), so it contributes nothing at this position.
        continue;
      }

      if (child.selected) {
        const childElements = processChildren(child.children);
        result.push({
          id: uniqueId(child.name, child.id),
          figma_node_id: child.id,
          type: elementType(child),
          rect: child.rect,
          visual_description: describeElement(child),
          text_content: child.figmaType === "TEXT" ? child.textContent : undefined,
          is_component_instance: child.isComponentInstance,
          ...(childElements.length > 0 ? { container: true as const } : {}),
          children: childElements,
        });
        continue;
      }

      // Not selected and not part of any composite group - skip through
      // without emitting anything for this node itself, but still recurse
      // so a selected descendant isn't blocked by an unchecked
      // organizational wrapper in between.
      result.push(...processChildren(child.children));
    }

    return result;
  }

  return processChildren(root.children);
}
