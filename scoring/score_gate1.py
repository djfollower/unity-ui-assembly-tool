#!/usr/bin/env python3
"""T1.11: Gate 1 scorer - Figma Reduction Fidelity.

Diffs a produced element-tree.json (parse -> reduce -> normalize output)
against the hand-labeled fixtures/golden-elements.json yardstick.

Metrics (spec thresholds):
  element recall     >= 90%  - % of golden elements the reduction produced
  element precision  >= 85%  - % of produced elements that are real (not noise)
  hierarchy accuracy >= 85%  - % of correctly-matched elements with the right parent

Verdict:
  PASS      recall >= 90% and precision >= 85% and hierarchy >= 85%
  FAIL      recall < 75% or precision < 70%
  SOFT PASS otherwise (the spec's literal "soft pass" is about held-out-frame
            hygiene, which a single-fixture slice can't test directly - this
            is the numeric middle zone between PASS and FAIL instead)

Matching is by figma_node_id, not the human/LLM-assigned "id" string - it's
the one field neither hand-labeling nor the LLM can typo or rename.

Usage: python3 scoring/score_gate1.py <produced-element-tree.json> [golden-elements.json]
"""

import json
import sys
from pathlib import Path

RECALL_PASS, PRECISION_PASS, HIERARCHY_PASS = 0.90, 0.85, 0.85
RECALL_FAIL, PRECISION_FAIL = 0.75, 0.70


def load(path):
    with open(path) as f:
        return json.load(f)


def flatten_with_parent(elements, parent_id="ROOT"):
    """Yields (element, parent_id) for every element in the tree, recursively."""
    for el in elements:
        yield el, parent_id
        yield from flatten_with_parent(el.get("children", []), el["figma_node_id"])


def index_by_node_id(tree_elements, exclude_synthetic=False):
    """figma_node_id -> (element, parent_id)

    exclude_synthetic drops golden-only fixture elements added for Gate 2's
    missing-recall testing (figma_node_id prefixed "synthetic:") - they don't
    exist in the real Figma frame, so reduce.ts can never produce them, and
    counting them against Gate 1 recall would be an unwinnable, unfair target.
    """
    return {
        el["figma_node_id"]: (el, parent_id)
        for el, parent_id in flatten_with_parent(tree_elements)
        if not (exclude_synthetic and el["figma_node_id"].startswith("synthetic:"))
    }


def score(produced_path, golden_path):
    produced_doc = load(produced_path)
    golden_doc = load(golden_path)

    produced = index_by_node_id(produced_doc["elements"])
    golden = index_by_node_id(golden_doc["elements"], exclude_synthetic=True)

    produced_ids = set(produced)
    golden_ids = set(golden)

    true_positive_ids = produced_ids & golden_ids
    missed_ids = golden_ids - produced_ids       # in golden, not produced -> hurts recall
    spurious_ids = produced_ids - golden_ids      # in produced, not golden -> hurts precision

    recall = len(true_positive_ids) / len(golden_ids) if golden_ids else 1.0
    precision = len(true_positive_ids) / len(produced_ids) if produced_ids else 1.0

    mis_parented_ids = []
    for node_id in true_positive_ids:
        _, produced_parent = produced[node_id]
        _, golden_parent = golden[node_id]
        if produced_parent != golden_parent:
            mis_parented_ids.append(node_id)
    hierarchy = (
        1.0 - (len(mis_parented_ids) / len(true_positive_ids))
        if true_positive_ids else 1.0
    )

    if recall < RECALL_FAIL or precision < PRECISION_FAIL:
        verdict = "FAIL"
    elif recall >= RECALL_PASS and precision >= PRECISION_PASS and hierarchy >= HIERARCHY_PASS:
        verdict = "PASS"
    else:
        verdict = "SOFT PASS"

    return {
        "recall": recall,
        "precision": precision,
        "hierarchy": hierarchy,
        "verdict": verdict,
        "true_positives": sorted(true_positive_ids),
        "missed": [(nid, golden[nid][0]["id"]) for nid in sorted(missed_ids)],
        "spurious": [(nid, produced[nid][0]["id"]) for nid in sorted(spurious_ids)],
        "mis_parented": [(nid, produced[nid][0]["id"]) for nid in sorted(mis_parented_ids)],
    }


def report(result):
    def pct(x):
        return f"{x * 100:.1f}%"

    print(f"{'Metric':<22}{'Value':<10}{'Target':<12}{'Result'}")
    print(f"{'Element recall':<22}{pct(result['recall']):<10}{'>= 90%':<12}"
          f"{'ok' if result['recall'] >= RECALL_PASS else 'below target'}")
    print(f"{'Element precision':<22}{pct(result['precision']):<10}{'>= 85%':<12}"
          f"{'ok' if result['precision'] >= PRECISION_PASS else 'below target'}")
    print(f"{'Hierarchy accuracy':<22}{pct(result['hierarchy']):<10}{'>= 85%':<12}"
          f"{'ok' if result['hierarchy'] >= HIERARCHY_PASS else 'below target'}")
    print()
    print(f"Verdict: {result['verdict']}")

    if result["missed"]:
        print(f"\nMissed ({len(result['missed'])}) - in golden, not produced:")
        for node_id, label in result["missed"]:
            print(f"  - {label} ({node_id})")

    if result["spurious"]:
        print(f"\nSpurious ({len(result['spurious'])}) - in produced, not golden:")
        for node_id, label in result["spurious"]:
            print(f"  - {label} ({node_id})")

    if result["mis_parented"]:
        print(f"\nMis-parented ({len(result['mis_parented'])}):")
        for node_id, label in result["mis_parented"]:
            print(f"  - {label} ({node_id})")


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)

    produced_arg = sys.argv[1]
    golden_arg = sys.argv[2] if len(sys.argv) > 2 else str(
        Path(__file__).resolve().parent.parent / "fixtures" / "golden-elements.json"
    )

    result = score(produced_arg, golden_arg)
    report(result)
    sys.exit(0 if result["verdict"] != "FAIL" else 1)
