#!/usr/bin/env python3
"""T2.6: Gate 2 scorer - Match Precision.

Diffs a produced match-result.json (candidates -> visual/structural signals
-> gate, see matcher/match.ts) against the hand-labeled
fixtures/golden-matches.json yardstick, using the catalog (for each matched
asset's render metadata) to classify results by sprite type.

Metrics (spec thresholds):
  false-accept rate (FAR)  <= 10%  - of AUTO-ACCEPTED elements, % given the
                                      WRONG asset (the dangerous number)
  auto-accept rate         >= 50%  - of the K truly-matchable elements, %
                                      auto-accepted (throughput)
  missing recall           >= 90%  - of the J truly-absent elements, %
                                      correctly flagged missing, not
                                      force-matched
  precision by type        no type's FAR > 15% (reported, not a hard gate -
                                      same treatment score_gate1.py gives
                                      hierarchy accuracy: in the PASS bar,
                                      not the FAIL one)

Verdict:
  PASS      FAR <= 10% and auto-accept >= 50% and missing-recall >= 90%
  FAIL      FAR > 25% or missing-recall < 70%
  SOFT PASS otherwise (same "numeric middle zone" convention as
            score_gate1.py - the spec's literal soft-pass language doesn't
            map cleanly onto a single-fixture slice)

"Auto-accepted" means produced status == "matched" (gate.ts's confident-
match branch) - "uncertain" is explicitly NOT auto-accepted (flagged for
human review) and doesn't count toward auto-accept rate or FAR, only toward
missing-recall if the golden element is genuinely absent (an "uncertain" is
not a false accept, but it's also not a correctly-flagged miss).

Sprite-type tags are non-exclusive (an asset can be both "9-slice" and
"tinted" and "resized-template" at once - see the real fixture's
button_upgrade -> UIElements__ButtonFrameTint) - "full-bitmap" is the
fallback tag when none of the others apply, so every matchable element gets
at least one tag.

Usage: python3 scoring/score_gate2.py <produced-match-result.json> [golden-matches.json] [catalog.json]
"""

import json
import sys
from pathlib import Path

FAR_PASS, AUTO_ACCEPT_PASS, MISSING_RECALL_PASS = 0.10, 0.50, 0.90
FAR_FAIL, MISSING_RECALL_FAIL = 0.25, 0.70
PER_TYPE_FAR_WARN = 0.15

TYPE_TAGS = ("full-bitmap", "9-slice", "tinted", "resized-template")


def load(path):
    with open(path) as f:
        return json.load(f)


def catalog_render_tags(catalog):
    """catalog asset id -> {"9-slice"?, "tinted"?} from real render metadata."""
    tags = {}
    for entry in catalog:
        render = entry["render"]
        entry_tags = set()
        if render["image_type"] in ("Sliced", "Tiled"):
            entry_tags.add("9-slice")
        if render.get("tint"):
            entry_tags.add("tinted")
        tags[entry["id"]] = entry_tags
    return tags


def type_tags_for(golden_entry, catalog_tags):
    tags = set(catalog_tags.get(golden_entry["matched_asset_id"], set()))
    if "resize" in golden_entry:
        tags.add("resized-template")
    if not tags:
        tags.add("full-bitmap")
    return tags


def is_correct(element_id, produced, golden):
    p = produced.get(element_id)
    g = golden.get(element_id)
    return (
        p is not None and g is not None
        and g["status"] == "matched"
        and p.get("matched_asset_id") == g["matched_asset_id"]
    )


def rate(numerator_ids, denominator_ids):
    return len(numerator_ids) / len(denominator_ids) if denominator_ids else None


def score(produced_path, golden_path, catalog_path):
    produced = {e["element_id"]: e for e in load(produced_path)}
    golden = {e["element_id"]: e for e in load(golden_path)}
    catalog_tags = catalog_render_tags(load(catalog_path))

    matchable_ids = {eid for eid, g in golden.items() if g["status"] == "matched"}  # K
    missing_ids = {eid for eid, g in golden.items() if g["status"] == "missing"}  # J
    unscored_ids = set(golden) - matchable_ids - missing_ids  # golden "uncertain" - not part of Gate 2's K/J split

    auto_accepted_ids = {eid for eid, p in produced.items() if p["status"] == "matched"}
    false_accept_ids = {eid for eid in auto_accepted_ids if not is_correct(eid, produced, golden)}
    correctly_auto_accepted_ids = matchable_ids & auto_accepted_ids
    correctly_flagged_missing_ids = {
        eid for eid in missing_ids if produced.get(eid, {}).get("status") == "missing"
    }

    far = rate(false_accept_ids, auto_accepted_ids)
    auto_accept_rate = rate(correctly_auto_accepted_ids, matchable_ids)
    missing_recall = rate(correctly_flagged_missing_ids, missing_ids)

    per_type = {}
    for tag in TYPE_TAGS:
        ids_of_type = {eid for eid in matchable_ids if tag in type_tags_for(golden[eid], catalog_tags)}
        accepted = ids_of_type & auto_accepted_ids
        wrong = accepted & false_accept_ids
        per_type[tag] = {
            "count": len(ids_of_type),
            "auto_accept_rate": rate(accepted, ids_of_type),
            "far": rate(wrong, accepted),
        }

    if far is not None and far > FAR_FAIL:
        verdict = "FAIL"
    elif missing_recall is not None and missing_recall < MISSING_RECALL_FAIL:
        verdict = "FAIL"
    elif (
        far is not None and far <= FAR_PASS
        and auto_accept_rate is not None and auto_accept_rate >= AUTO_ACCEPT_PASS
        and missing_recall is not None and missing_recall >= MISSING_RECALL_PASS
    ):
        verdict = "PASS"
    else:
        verdict = "SOFT PASS"

    return {
        "far": far,
        "auto_accept_rate": auto_accept_rate,
        "missing_recall": missing_recall,
        "per_type": per_type,
        "verdict": verdict,
        "k": len(matchable_ids),
        "j": len(missing_ids),
        "false_accepts": sorted(
            (eid, produced[eid].get("matched_asset_id"), golden.get(eid, {}).get("matched_asset_id"))
            for eid in false_accept_ids
        ),
        "missed_recall": sorted(missing_ids - correctly_flagged_missing_ids),
        "unscored": sorted(unscored_ids),
    }


def report(result):
    def pct(x):
        return "n/a" if x is None else f"{x * 100:.1f}%"

    def ok(x, target_met):
        return "n/a" if x is None else ("ok" if target_met else "below target")

    print(f"{'Metric':<24}{'Value':<10}{'Target':<12}{'Result'}")
    print(f"{'False-accept rate':<24}{pct(result['far']):<10}{'<= 10%':<12}"
          f"{ok(result['far'], result['far'] is not None and result['far'] <= FAR_PASS)}")
    print(f"{'Auto-accept rate':<24}{pct(result['auto_accept_rate']):<10}{'>= 50%':<12}"
          f"{ok(result['auto_accept_rate'], result['auto_accept_rate'] is not None and result['auto_accept_rate'] >= AUTO_ACCEPT_PASS)}")
    print(f"{'Missing recall':<24}{pct(result['missing_recall']):<10}{'>= 90%':<12}"
          f"{ok(result['missing_recall'], result['missing_recall'] is not None and result['missing_recall'] >= MISSING_RECALL_PASS)}")
    print(f"\n(K = {result['k']} truly-matchable golden elements, J = {result['j']} truly-absent)")

    print(f"\n{'Type':<18}{'Count':<8}{'Auto-accept':<14}{'FAR'}")
    for tag, stats in result["per_type"].items():
        far_str = pct(stats["far"])
        warn = " <-- above 15%" if stats["far"] is not None and stats["far"] > PER_TYPE_FAR_WARN else ""
        print(f"{tag:<18}{stats['count']:<8}{pct(stats['auto_accept_rate']):<14}{far_str}{warn}")

    print(f"\nVerdict: {result['verdict']}")

    if result["false_accepts"]:
        print(f"\nFalse accepts ({len(result['false_accepts'])}):")
        for eid, got, expected in result["false_accepts"]:
            print(f"  - {eid}: matched {got!r}, golden expected {expected!r}")

    if result["missed_recall"]:
        print(f"\nMissed (should have been flagged missing, wasn't) ({len(result['missed_recall'])}):")
        for eid in result["missed_recall"]:
            print(f"  - {eid}")

    if result["unscored"]:
        print(f"\nGolden status neither matched nor missing (not part of K/J) ({len(result['unscored'])}):")
        for eid in result["unscored"]:
            print(f"  - {eid}")


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)

    repo_root = Path(__file__).resolve().parent.parent
    produced_arg = sys.argv[1]
    golden_arg = sys.argv[2] if len(sys.argv) > 2 else str(repo_root / "fixtures" / "golden-matches.json")
    catalog_arg = sys.argv[3] if len(sys.argv) > 3 else str(repo_root / ".cache" / "catalog.json")

    result = score(produced_arg, golden_arg, catalog_arg)
    report(result)
    sys.exit(0 if result["verdict"] != "FAIL" else 1)
