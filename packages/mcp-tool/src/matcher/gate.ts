// T2.5: two-factor + margin (R5). Combines visual + structural signals:
// agreement check, margin-over-runner-up check, emits matched/uncertain/missing
// per match-result.schema.json. Includes the resize-safety check (R14).
//
// Thresholds tuned against a real topK=10 run on the golden fixture, using
// the pure-JS pixel-based visualSignal (see visual-signal.ts's own notes on
// why this replaced an LLM-based one) - not the original placeholder
// guesses from before real data existed.
//
// Candidate SELECTION (which of the topK is "best") originally ranked by
// visual score alone, with structural only used for the "agree" cross-
// check - changed after real data showed visual-only selection picks the
// WRONG candidate in most of this fixture's real matched cases (visual is
// a coarser signal than the LLM-based one it replaced, and loses more
// often to close, wrong-but-visually-similar competitors), while
// structural signal (name/description overlap) independently picks
// correctly more often for those same cases. Selection now ranks by a
// combined score - see combinedScore below.
//
// Re-tuned (graduation-phase session) after the catalog's thumbnail-
// rendering bugs were fixed (see HANDOFF.md "Thumbnail rendering bug,
// parts 2-4") - the previous WEIGHT_VISUAL=0.6/WEIGHT_STRUCTURAL=0.4 split
// was calibrated against the OLD, buggy thumbnails and went stale. Found
// by collecting raw per-candidate visual/structural scores once (real
// renders, expensive) and then sweeping weight/threshold combinations
// cheaply against fixtures/golden-matches.json. Root cause of why more
// STRUCTURAL weight specifically helps: several of this fixture's near-
// miss cases are near-visual-DUPLICATES (e.g. `ButtonFrame`/
// `ButtonFrameTint`/`button_frame_x` are three different red/blue frame
// sprites that all score visual ~0.345-0.347 against the same element) -
// min-max normalization (see normalizedScores below) stretches that
// noise-level ~0.002 spread across nearly the FULL 0-1 normalized range,
// so under the old 60% visual weighting that noise was drowning out a
// genuinely-informative structural signal with a real ~40% relative
// spread (0.158 vs 0.224) for the same candidates. Verified: flips
// `button_continue`'s selection from wrong (`UIElements__button_green`,
// a coincidentally-similar green button) to correct
// (`UIElements__ButtonFrame`) and now auto-accepts it; also corrects
// `button_x_base`'s selection (still lands `uncertain`, appropriately -
// its margin is razor-thin at ~0.003). FAR stayed 0% and missing-recall
// 100% across the entire sweep - auto-accept rose from the un-retuned
// 28.6% (this session's regressed baseline against the fixed thumbnails)
// to 42.9%, matching this file's own previously-documented raw-matcher
// baseline. Chose the least-aggressive tied config (0.4/0.6 over 0.3/0.7
// or 0.2/0.8, which scored identically on this 10-element fixture) -
// deliberately not chasing the most extreme option a fixture this small
// can't actually distinguish from noise.

import type { CatalogEntry, ElementTree, MatchResult } from "@ui-assembler-slice/contracts";

export interface ScoredCandidate {
  candidate: CatalogEntry[number];
  visual: number;
  structural: number;
}

// Applied to best.visual (RAW, not the normalized combined score) - an
// absolute "is this good enough at all" gate. Deliberately NOT applied to
// the combined/normalized score: normalizing per-element (see
// normalizedScores below) always stretches the best of a candidate set
// toward 1.0 regardless of absolute quality, which would make a
// genuinely-missing element's best (also-bad) candidate look artificially
// confident - the missing/matched decision needs an absolute yardstick,
// selection needs a relative one, and conflating them was the bug this
// comment is here to prevent reintroducing.
const MATCH_VISUAL_THRESHOLD = 0.35;
const MISSING_VISUAL_THRESHOLD = 0.24;
// Margin is measured on the combined (normalized, 0-1 relative-to-this-
// element's-candidates) score, unlike the two thresholds above - it's
// inherently a relative "how much better than the runner-up" question.
const MARGIN_THRESHOLD = 0.05;
const WEIGHT_VISUAL = 0.4;
const WEIGHT_STRUCTURAL = 0.6;
// A Simple/Filled resize is "safe" if it's roughly uniform (doesn't visibly
// squash/stretch) - Sliced/Tiled don't need this check at all, they're
// designed to absorb arbitrary resizes (see below).
const ASPECT_DISTORTION_TOLERANCE = 0.1;
// Sizes within this many canvas_reference units of native are treated as
// "no resize" - avoids a spurious resize block from float rounding on an
// otherwise-exact size match.
const RESIZE_EPSILON = 0.5;

type Rect = ElementTree["elements"][number]["rect"];

function computeResize(target: Rect, candidate: CatalogEntry[number]): MatchResult[number]["resize"] {
  const native = candidate.render.native_size;
  const widthDiff = Math.abs(target.w - native.w);
  const heightDiff = Math.abs(target.h - native.h);
  if (widthDiff <= RESIZE_EPSILON && heightDiff <= RESIZE_EPSILON) {
    return undefined;
  }

  const sizeDeltaTo = { w: target.w, h: target.h };
  const { image_type } = candidate.render;
  if (image_type === "Sliced" || image_type === "Tiled") {
    // The entire point of 9-slicing/tiling is absorbing an arbitrary target
    // size without visible distortion - no aspect check needed.
    return { safe: true, size_delta_to: sizeDeltaTo };
  }

  // Simple (and Filled, which doesn't occur in this catalog but behaves the
  // same way here): stretches to fill exactly, so a non-uniform scale
  // (different factor per axis) is visible squash/stretch. A uniform-ish
  // scale (same factor both axes, within tolerance) just looks like the
  // same art at a different size - still safe.
  const scaleX = target.w / native.w;
  const scaleY = target.h / native.h;
  const maxScale = Math.max(scaleX, scaleY);
  const aspectDelta = maxScale === 0 ? 0 : Math.abs(scaleX - scaleY) / maxScale;
  return { safe: aspectDelta <= ASPECT_DISTORTION_TOLERANCE, size_delta_to: sizeDeltaTo };
}

// Min-max normalizes one signal across this element's own candidate set -
// makes visual (pixel-metric scale) and structural (token-overlap scale)
// commensurable for COMBINING into a selection score, without needing to
// know either signal's "true" global range. Ties (zero range) normalize to
// a neutral 0.5 for everyone rather than an arbitrary 0 or 1.
function normalizedScores(scoredCandidates: ScoredCandidate[], key: "visual" | "structural"): number[] {
  const values = scoredCandidates.map((c) => c[key]);
  const min = Math.min(...values);
  const max = Math.max(...values);
  const range = max - min;
  return values.map((v) => (range > 0 ? (v - min) / range : 0.5));
}

export function gate(
  element: ElementTree["elements"][number],
  scoredCandidates: ScoredCandidate[],
): MatchResult[number] {
  if (scoredCandidates.length === 0) {
    return {
      element_id: element.id,
      status: "missing",
      matched_asset_id: null,
      signals: { visual: 0, structural: 0, agree: false, margin: 0 },
    };
  }

  const normVisual = normalizedScores(scoredCandidates, "visual");
  const normStructural = normalizedScores(scoredCandidates, "structural");
  const combined = scoredCandidates.map((c, i) => ({
    ...c,
    combinedScore: normVisual[i] * WEIGHT_VISUAL + normStructural[i] * WEIGHT_STRUCTURAL,
  }));
  const byCombined = [...combined].sort((a, b) => b.combinedScore - a.combinedScore);
  const best = byCombined[0];
  const runnerUp = byCombined[1];
  const margin = best.combinedScore - (runnerUp?.combinedScore ?? 0);

  // Corroboration, not selection: does at least one of the two signals,
  // taken on its OWN (not combined), independently pick the same winner
  // the combined score settled on? A stricter "both signals must agree"
  // check very rarely fires once selection itself blends the two signals
  // (by construction, the combined winner is often each signal's runner-up
  // rather than either one's own #1) - "at least one independently agrees"
  // keeps this a meaningful check without being so strict it never passes.
  const bestByVisual = scoredCandidates.reduce((a, b) => (b.visual > a.visual ? b : a));
  const bestByStructural = scoredCandidates.reduce((a, b) => (b.structural > a.structural ? b : a));
  const agree = bestByVisual.candidate.id === best.candidate.id || bestByStructural.candidate.id === best.candidate.id;

  const signals = {
    visual: best.visual,
    structural: best.structural,
    agree,
    margin,
  };

  if (best.visual < MISSING_VISUAL_THRESHOLD) {
    return { element_id: element.id, status: "missing", matched_asset_id: null, signals };
  }

  const resize = computeResize(element.rect, best.candidate);
  const isConfidentMatch = agree && best.visual >= MATCH_VISUAL_THRESHOLD && margin >= MARGIN_THRESHOLD;

  return {
    element_id: element.id,
    status: isConfidentMatch ? "matched" : "uncertain",
    matched_asset_id: best.candidate.id,
    signals,
    ...(resize ? { resize } : {}),
  };
}
