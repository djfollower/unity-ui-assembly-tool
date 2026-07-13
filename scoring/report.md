# Vertical Slice — Gate Readout & Go/No-Go

Scope: T2.8 per `HANDOFF.md`. Covers the two empirical risk gates the slice exists to measure
(Figma reduction fidelity, match precision) per the vertical-slice spec's Definition of Done.
Does **not** cover the assembled `.prefab` (Week 3, not started) — the spec's own thesis is that
Gate 1 + Gate 2 are what decide *whether* to build the assembler, not the other way around, so a
go/no-go call is meaningful without it.

Reproduce:
```
python3 scoring/score_gate1.py .cache/element-tree.json
python3 scoring/score_gate2.py .cache/match-result.json
```

## Gate 1 — Figma Reduction Fidelity

Reduced logical-element set (`.cache/element-tree.json`, a real `reduce.ts` run against the real
"Lose Screen" Figma frame) vs. the hand-labeled golden set (`fixtures/golden-elements.json`, 7 real
elements + `button_x`'s 3 hand-authored composite children, excluded from this gate).

| Metric | Value | Target | Result |
|---|---|---|---|
| Element recall | 100.0% | ≥ 90% | ok |
| Element precision | 100.0% | ≥ 85% | ok |
| Hierarchy accuracy | 100.0% | ≥ 85% | ok |

**Verdict: PASS.**

Caveat worth stating plainly: this is one real frame. The spec's own literal "soft pass" criterion
for Gate 1 is about held-out-frame hygiene ("targets met only on well-named frames") — a
single-fixture slice can't test that dimension at all, so a clean PASS here says the reduction
pass works on *this* frame, not that it's robust to messier files. Not a reason to distrust the
number; a reason not to over-read it.

## Gate 2 — Match Precision

Two-factor gate (`packages/mcp-tool/src/matcher/`) on the golden element set vs. the single-feature
catalog (`.cache/catalog.json`, 65 entries), topK=10. K=7 truly-matchable elements, J=3
truly-absent.

| Metric | Value | Target | Result |
|---|---|---|---|
| False-accept rate | **0.0%** | ≤ 10% | ok |
| Auto-accept rate | 42.9% | ≥ 50% | below target |
| Missing recall | **100.0%** | ≥ 90% | ok |

| Sprite type | Count | Auto-accept | FAR |
|---|---|---|---|
| full-bitmap | 3 | 33.3% | 0.0% |
| 9-slice | 4 | 50.0% | 0.0% |
| tinted | 1 | 100.0% | 0.0% |
| resized-template | 2 | 100.0% | 0.0% |

**Verdict: SOFT PASS**, by `score_gate2.py`'s own numeric-middle-zone convention (documented in its
docstring) rather than the spec's literal soft-pass band (FAR 10–25%) — worth being precise about,
since our shortfall isn't FAR at all, it's auto-accept rate.

The two auto-accept misses (`icon_glow`, `button_x_frame`) plus 2 more within the same composite
group (`button_x_base`, `button_x_icon`) all land on `uncertain`, not a wrong `matched` — zero false
accepts across the whole run. Each is a already-diagnosed, already-documented root cause (see
`HANDOFF.md`), not an open mystery:
- `icon_glow` → best real candidate is `UIElements__icon_heart`, a known generic-token collision
  (`structuralSignal` has no whole-catalog IDF context to rank the correct `ui_img_glow` above it -
  T2.4's original finding, still unresolved).
- `button_x_frame` → best real candidate is `UIElements__button_x` instead of the correct
  `UIElements__button_frame_x` - verified by rendering both composited candidates side by side
  (see "Composite crop-sharing: RESOLVED" in `HANDOFF.md`): the two are genuinely near-identical
  plain red rounded frames, and the coarse SSIM+histogram visual signal can't discriminate them.
- `button_x_base`/`button_x_icon` → both land on the *correct* asset id (`UIElements__button_x`,
  `UIElements__icon_x`) but with too thin a margin to clear the confident-match threshold - a
  calibration shortfall, not a wrong-answer one.

Both root causes this readout's two prior fixes targeted are closed and verified end-to-end against
this exact run: **synthetic-element crop quality** (previously the dominant driver - FAR was 33.3%
and missing-recall 66.7% before it) and **composite children sharing one crop** (previously
`button_x_base` came back `missing` with an implicitly wrong match; now lands on the correct asset,
just short of the margin bar).

## Go / No-Go

**Recommendation: proceed to Week 3 (the assembler), behind the constraint the spec's own soft-pass
language names — mandatory human review on `uncertain` results.**

Reasoning, reading the two gates as the spec intends (independently, since Gate 2 runs on golden
elements, not reduction output):

- **Gate 1 is a clean PASS.** Figma reduction is not the risk on this frame.
- **Gate 2's dangerous number is 0%.** The whole point of the two-factor gate is to never
  confidently ship a wrong asset - it didn't, once, across every real matched or absent case in
  this fixture, including the tinted and composite cases that are specifically designed to stress
  it. Missing-recall is also perfect: every truly-absent element (including the two
  fixture-authoring-dependent synthetic ones) was correctly flagged, none force-matched.
- **The shortfall is throughput, not safety.** Auto-accept rate (42.9%) is below the 50% target
  because the gate is conservative on 4 of 10 elements rather than wrong on any of them - all 4
  have a specific, already-understood cause (above), and none of the 4 causes is "the two-factor
  design doesn't work" - they're a coarse-pixel-metric discrimination limit (twice) and a
  margin-threshold calibration gap (twice, both on the SAME element - `button_x`'s children -
  which is itself a fixture edge case: a single hand-authored composite decomposition, not a
  general pattern observed anywhere else in the 65-entry catalog).
- This is close to exactly the spec's own description of a soft pass: *"Usable behind mandatory
  review; tighten thresholds before scaling."* The one deviation from the spec's literal wording is
  that our gap isn't FAR - it's auto-accept - so the fix path for a future push toward full PASS is
  narrower than a general FAR problem would imply: it's IDF-aware structural scoring for
  `icon_glow`-style collisions, and either a `button_x`-specific decomposition or a real Figma
  layer structure that doesn't require hand-authored composite fixtures at all, not a wholesale
  re-tune of the matcher.

**What "mandatory review" means concretely for Week 3**: the assembler should treat `matched` as
auto-place, `uncertain` as auto-place-with-a-flag-for-human-review (not auto-reject - all 4
`uncertain` cases in this run had the right general idea, 2 had exactly the right asset), and
`missing` as skip-and-flag. This fixture never needed to test that policy end-to-end since the
assembler doesn't exist yet - worth confirming against a slightly larger/messier frame once it
does, since this run's K=7/J=3 is small enough that single-element misses move the percentages a
lot (e.g. one more correct auto-accept alone would clear the 50% bar).

## What this readout does not cover

- The assembled `.prefab` (Week 3) - the remaining Definition-of-Done item, not part of this gate
  readout by design (see scope note above).
- Robustness to a second, messier Figma frame or a second, larger catalog - both gates are
  measured on one fixture each, per the spec's own scope cut for this slice.
