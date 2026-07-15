# Handoff

Session summary for picking this back up fresh. See `README.md` for setup/commands; this file is
about *state* and *things that aren't obvious from reading the code*.

## Where things stand

**Gate 1: PASS** (Week 1 complete). **Gate 2: PASS** (was SOFT PASS - see
below, upgraded after Week 3's real prefab review caught a golden-fixture bug
and validated 3 `uncertain` promotions). **Week 3 (assembler) implemented and
verified against the real fixture, including a real live Editor review by the
user** - see "Week 3: the assembler" below. **T3.6 DONE**: `scoring/report.md`
now carries the Week 3 side-by-side of `fixtures/frame-export.png` and
`fixtures/assembled-prefab.png` (user-captured screenshot of
`Assets/_Generated/UIAssembler/178_35186.prefab`) with an explicit Week 3
Go/No-Go: PASS. The vertical slice is complete end-to-end.

Done: T1.1-T1.6, T1.8-T1.12, T2.1-T2.6 (T2.7 - threshold tuning - now also substantially done, see
below), plus six not-in-the-original-numbering additions across sessions: an orchestrator, an
agent-adapter architecture change, composite-element support (+ this session's follow-up fixing its
crop-sharing gap), a real Unity-side thumbnail-rendering bug fix, and a pivot from LLM-based to
pure-JS visual matching (all below). **A real, scored topK=10 Gate 2 run now exists** against the
fixed catalog - see "Pure-JS visual signal + gate redesign" below for the numbers and history. Both
previously-identified root causes of that run's FAIL are now fixed - **composite-children-sharing-
one-crop** (see "Composite crop-sharing: RESOLVED") and **synthetic-element crop quality** (see
"Synthetic-element crop quality: RESOLVED", the one that actually moved the headline numbers: FAR
33.3%->0%, missing-recall 66.7%->100%, verdict FAIL->SOFT PASS). Only auto-accept rate (42.9%,
target >=50%) is still below its PASS bar, driven by the residual `icon_glow`/`button_x_frame`
coarse-visual-signal limitations documented in both RESOLVED sections above. **T2.8 (Gate 2 readout
/ go-no-go write-up) is now DONE** - see `scoring/report.md`: Gate 1 PASS (100/100/100), Gate 2 SOFT
PASS (FAR 0%, auto-accept 42.9%, missing-recall 100%), recommendation is to proceed to Week 3 behind
mandatory review on `uncertain` results, per the spec's own soft-pass language. **Next task: Week 3
(the assembler)**, or further improvement into `T2.7`-style threshold/signal tuning territory
(diminishing, more speculative returns per `scoring/report.md`'s own analysis) if the goal is
pushing Gate 2 to a full PASS before building further - see "What's not done yet".

## Agent-agnostic LLM adapter (session addition, not in the original T-numbering)

User pushback that reshaped this: didn't want a tool that costs money per run on top of not being
100% automated/accurate - reasonably unwilling to pay a separate metered API bill for a dev tool
whose whole point is saving them time. Also wanted the tool to not be hard-locked to one AI
provider. Resolved by building `packages/mcp-tool/src/agent/` - an `AgentAdapter` interface
(`isAvailable()` + `complete(prompt, imagePaths?)`) with one implementation, `claude-cli-adapter.ts`
- shells out to the `claude` CLI and rides on its existing subscription/OAuth login instead of a
metered key (same trick `reduce.ts` already used for T1.9; now shared and generalized so
`visual-signal.ts` uses it too instead of `@anthropic-ai/sdk`, which has been removed as a
dependency entirely). `reduce.ts` was refactored to use the same shared adapter (was carrying its
own private copy of the same spawn/parse logic - real duplication, not just similar-looking code).
Unity AI Assistant (`Unity.AI.Assistant.Editor.Api`, `RunHeadless` + `AttachedContext.
AddImageContent(Texture, ...)`) was researched as a candidate second adapter per the user's
priority list (Claude + Unity's own AI, not other coding-agent CLIs) - looks technically plausible
from Unity's docs (async, no Assistant window required, takes a `Texture` directly) but NOT
implemented: user doesn't have a license to test against, and it would run Editor-side (a
genuinely different adapter shape - inside the Unity process, not a Node-spawned CLI), not just
another list entry. `getAgentAdapter()` in `registry.ts` is the extension point if that changes.

**Two real bugs found and fixed by actually running this against the `claude` CLI for real** (this
environment has it available and authenticated, so - unlike previous sessions - this could finally
be exercised end-to-end, not just validated with simulated scores):
1. A nested `claude -p` doesn't get filesystem read access to arbitrary paths for free. First
   version wrote the crop/render temp images to `node:os`'s `tmpdir()`; the nested CLI refused to
   read them ("I don't have permission to read those temp files"). Fixed with `--add-dir
   <parent-dirs-of-the-image-paths>`, which explicitly grants the session access. Confirmed this
   is a real constraint of running a nested agent CLI, not just this dev sandbox - a genuine design
   point worth remembering for any adapter that shells out to another CLI.
2. **`render-candidate.ts` crashed on a real catalog entry** (`sharp: extract_area: bad extract
   area`) that none of T2.3's earlier hand-picked test cases (`button_frame_blue`, `icon_heart`,
   `ButtonFrameTint`, `button_green`) happened to trigger. Root cause: regions were built by
   rounding each rect's `x` and `width` independently; for `UIElements__ui_popup_frame` specifically,
   the sprite's content fills its entire 256x256 canonical thumbnail with zero transparent
   padding, so the right-column boundary (`contentBBox.x + contentBBox.w - srcBorder.right` =
   187.5, width 68.5) rounds each half up independently (188 + 69 = 257), one pixel past the
   image's actual 256px edge - `sharp.extract()` throws rather than clamping. Every OTHER catalog
   entry has some transparent margin, which is why this never surfaced in hand-picked tests. Fixed
   by rounding the 4 shared boundary coordinates ONCE per axis (not each rect's x/width pair
   separately) and deriving widths from consecutive rounded edges - guarantees adjacent slices
   tile exactly and the outer edge can't overshoot, since it's now `Math.round(exact-integer-pixel-
   count)`, a no-op. Added a defensive clamp in `extractRegion` too as a second layer. Verified by
   fuzzing `renderCandidateAtSize` against all 65 real catalog entries × 12 representative target
   sizes (780 renders): failed on `ui_popup_frame` at every size before the fix, 0 failures after.
   **Lesson reinforced**: hand-picked visual spot-checks (what T2.3 relied on) don't cover the
   input space the way even a cheap local fuzz pass does - worth doing this kind of full-catalog
   sweep earlier next time a renderer/parser touches real heterogeneous data.

**First real end-to-end run** (topK=3, deliberately small/cheap for a first smoke test - not the
tuning-quality topK=10 run T2.7 wants): `python3 scoring/score_gate2.py` on the real output gave
FAR=50%, auto-accept=40%, missing-recall=100%, verdict FAIL. Read those numbers with real caveats,
not at face value:
- Only ONE actual matcher miss: `icon_glow` -> `UIElements__icon_heart` (wrong). This is the
  already-documented, already-understood `structuralSignal` generic-token-collision limitation
  (T2.4 notes) - `ui_img_glow` ranks lower than `icon_heart` on plain Jaccard, and at topK=3 (vs.
  the topK=10 this was checked against before) it may not even have been in the candidate set.
- `button_upgrade` came back `missing` (golden wants `matched`) - but this is a **fixture gap, not
  a matcher bug**: `button_upgrade` is one of 3 SYNTHETIC golden elements (fabricated for Gate 2
  tint-testing, `synthetic:` `figma_node_id` prefix) with a made-up `rect` that doesn't correspond
  to any real content in `fixtures/frame-export.png`. Checked the actual crop `visualSignal` sees
  for it: it's a screenshot of a "2:35" HUD timer, not a tinted button - there was never a real
  button drawn at that fabricated location. `visualSignal`'s whole design depends on cropping real
  mockup pixels as ground truth (see element-crop.ts's rationale for why - avoiding per-element
  Figma image-export calls); that assumption silently breaks for synthetic elements, and nothing
  currently guards against it. `button_share` (also synthetic) correctly landed on `missing` anyway
  since there's no real asset for it either way, so this only actually distorts the ONE synthetic
  case (`button_upgrade`) that's supposed to resolve to a real match.
- `button_x` also came back `missing` (golden wants `matched`) - **not a matcher bug, and NOT
  stale-cache either (that theory was wrong - see below)**. `button_x`'s crop from the real mockup
  is correct (a clean red X-close icon with a white X glyph, checked visually). But
  `UIElements__button_x`'s catalog thumbnail is visibly wrong: a pink scalloped/doubled shape,
  nothing like an X button.
- **Rebuilt the catalog for real** (`scripts/build-catalog.sh`, Unity 2022.3.62f2 + the Melon
  project are both actually available in this environment - the earlier "needs Unity Editor access
  this environment doesn't have" note was wrong). Result: **byte-for-byte identical to the old
  catalog** (same MD5). This is a real, deterministic, currently-active bug in the Unity-side
  rendering code, not stale data - correcting the earlier diagnosis.
- Dug further: `button_x.png`'s raw source asset (read directly from the Melon project) is a THIRD
  different image again - a plain rounded frame with no X glyph on it at all (the glyph is likely a
  separate composited layer/sprite in the real game, e.g. a candidate `icon_x` sprite - the catalog
  has one). So neither the raw asset nor the rendered thumbnail match the Figma mockup's single
  composited "X button" image - two separate, real issues layered on each other.
- Spot-checked other sprite thumbnails against this pattern: `UIElements__button_settings` (also
  128x128) shows the exact same duplicated/scalloped artifact; `UIElements__button_red` and
  `UIElements__button_frame_x` (256x256 and a flat/plain 128x128 respectively) render cleanly.
  **Working hypothesis**: a texture wrap-mode artifact. `RenderedThumbnail.cs` upscales any sprite
  smaller than the canonical thumbnail size (128x128 native -> 256x256 canonical here is a 2x
  upscale; 256x256 native needs no upscale) - if that texture's import Wrap Mode is "Repeat"
  instead of "Clamp", GPU sampling near the 9-slice border can wrap around and sample the texture a
  second time, producing exactly this "duplicated" look, magnified enough to be visible only once
  upscaled. Not yet fixed or confirmed via a live Editor debug session (would want to check the
  actual Wrap Mode import setting on `button_x.png`/`button_settings.png` vs. `button_red.png` to
  confirm before changing `RenderedThumbnail.cs` or the assets' import settings) - flagging as the
  next concrete step rather than guessing further.
- Net: the matcher logic itself only has the one already-known, already-scoped miss (`icon_glow`).
  The FAIL verdict is real but driven mostly by things outside `packages/mcp-tool/src/matcher/`'s
  own logic this run - fix the wrap-mode bug (affects an unknown-but-nonzero number of the 62
  sprite entries, not just `button_x`) and address synthetic elements' crops before reading a
  future run's FAIL as "the matcher doesn't work." This run's output was scratch-only (topK=3, not
  worth keeping as the canonical `.cache/match-result.json`) - rerun once the render bug is fixed.

T2.6 (`score_gate2.py`) implementation notes:
- Mirrors score_gate1.py's structure/CLI convention: `python3 scoring/score_gate2.py
  <produced-match-result.json> [golden-matches.json] [catalog.json]`, defaults to
  `fixtures/golden-matches.json` and `.cache/catalog.json`, exit code 1 on FAIL. Needs the catalog
  (not just produced+golden) to classify each golden match's real asset into sprite-type tags
  (`full-bitmap`/`9-slice`/`tinted`/`resized-template`) for the per-type breakdown - tags are
  non-exclusive (e.g. `button_upgrade`'s `UIElements__ButtonFrameTint` is 9-slice AND tinted AND
  resized-template all at once), `full-bitmap` is the fallback when none of the others apply.
- "Auto-accepted" = produced `status == "matched"` only - `"uncertain"` deliberately doesn't count
  toward auto-accept rate or FAR (not force-matched, flagged for review instead), and doesn't count
  toward missing-recall either unless the golden element is genuinely absent AND produced status is
  literally `"missing"` (an `"uncertain"` on a truly-absent element is neither a false accept nor a
  correctly-flagged miss - it's between the two, and none of Gate 2's three headline metrics credit
  it either way).
- The spec's "no type > 15% FAR" check is reported (with a `<-- above 15%` flag on the per-type
  table) but NOT folded into the PASS/FAIL verdict - same treatment score_gate1.py gives hierarchy
  accuracy (part of the PASS bar, not the FAIL one), since the spec's own verdict-row prose only
  cites overall FAR/auto-accept/missing-recall.
- Verified without a real matcher run (no API key yet, same blocker as T2.5): scored
  `golden-matches.json` against itself - correctly produces 0% FAR, 100% auto-accept, 100%
  missing-recall, PASS, and a per-type breakdown matching hand-derived expectations exactly
  (full-bitmap: 2, 9-slice: 3, tinted: 1, resized-template: 2, out of 5 real matched golden
  elements). Also hand-crafted a deliberately-flawed match-result (one wrong asset, one
  force-matched truly-absent element, one correct match downgraded to "uncertain") and confirmed:
  FAR/auto-accept/missing-recall/per-type-FAR all computed exactly right, the "uncertain" entry was
  correctly excluded from both auto-accept and false-accept counting, and the verdict correctly
  came back FAIL (exit code 1) for the introduced FAR/missing-recall breach.

**Orchestrator** (`packages/mcp-tool/src/matcher/match.ts` + `cli.ts`'s `match` subcommand,
`catalog/load-catalog.ts` now implemented too): identified as a genuine gap while starting T2.6 -
score_gate2.py needs a produced `match-result.json` to diff against `golden-matches.json`, and
nothing wired `candidates()` -> `visualSignal()`/`structuralSignal()` -> `gate()` into one pass
over a whole element tree. Not in the original T2.x numbering; built it as a prerequisite.
- `matchElementTree(elementTree, catalog, options)`: filters to "matchable" elements (excludes
  `type: "text"` - those become TMP text objects in Week 3's assembler, not Image/prefab
  instances; confirmed against the real fixture that this produces exactly `golden-matches.json`'s
  8 entries, in the same order, from `golden-elements.json`'s 10). Sequential across elements,
  parallel (bounded by `topK`, ~10) across candidates within one element - keeps peak concurrent
  Claude calls bounded without a general-purpose concurrency limiter, which this fixture's scale
  doesn't need.
- `cli.ts` now has one real subcommand: `ui-assembler match <element-tree.json> <catalog.json>
  [output.json]` (default output `.cache/match-result.json`), loading `.env` the same way
  `scripts/*.mjs` already do (small copy-pasted loader, not a `dotenv` dependency - matches
  existing convention). `fetch-frame`/`reduce`/`build-catalog-descriptions` are still unwired -
  out of scope here, and not blocking (fixtures/golden-elements.json is itself a valid
  `element-tree.json` and stands in for now).
- Verified end-to-end up to the expected stopping point: ran `ui-assembler match
  fixtures/golden-elements.json .cache/catalog.json` for real - arg parsing, `.env` loading,
  `loadCatalog()`, the matchable-element filter, `candidates()`, and `structuralSignal()` all ran
  correctly against real data and it stopped exactly at `visualSignal()`'s missing-API-key error
  (expected, see T2.3 notes) - nothing further to verify without a real key.

T2.5 (`gate.ts`) implementation notes:
- `agree` is computed by RANK, not by both signals clearing the same fixed threshold: does the
  candidate visual-signal ranks #1 also rank #1 by structural-signal? This was a deliberate
  design call, not the obvious literal reading of "agreement check" - visual (an LLM 0-1
  similarity score) and structural (T2.4's token-overlap statistics, which top out much lower even
  for genuinely correct matches - e.g. `icon_heart`'s own correct match only scored ~0.25, see
  T2.4 notes) live on incomparable absolute scales, so thresholding both the same way would make
  "agree" fire almost never. Asking "do both signals independently pick the same winner" sidesteps
  the scale mismatch entirely.
- `margin` is the visual-score gap between the best and runner-up candidate specifically (not a
  combined score) - visual is the well-calibrated 0-1 signal; structural only feeds `agree`.
- Resize block matches `size_delta_to` exactly as used in `golden-matches.json` (confirmed by
  reading the fixture, not just the schema text): despite the name, `size_delta_to` is the
  element's target `{w, h}`, not an actual delta - e.g. `button_continue`'s entry is
  `size_delta_to: {w: 838, h: 285}`, exactly its `rect`, not `rect - native_size`. `safe` is always
  `true` for Sliced/Tiled (the entire point of 9-slicing is absorbing arbitrary resizes); for
  Simple/Filled, `safe` checks whether the two axes scale by roughly the same factor (a uniform-ish
  resize doesn't visibly distort, a non-uniform one squashes/stretches) - this branch has no real
  fixture example to validate against yet (both real matched-and-resized golden cases,
  `button_continue`→`button_green` and `button_upgrade`→`ButtonFrameTint`, are Sliced), so it's a
  schema-description-only implementation, unverified against real data.
- Thresholds (`MATCH_VISUAL_THRESHOLD=0.75`, `MISSING_VISUAL_THRESHOLD=0.35`,
  `MARGIN_THRESHOLD=0.15`, `ASPECT_DISTORTION_TOLERANCE=0.1`) are first-pass guesses, not tuned
  against real Gate 2 numbers - genuinely can't be until `visualSignal()` is callable end-to-end
  (needs a real `ANTHROPIC_API_KEY`, see T2.3 notes). Validated the gate's LOGIC (branching,
  resize, margin/agree computation) with hand-built scenarios using real `structuralSignal()`
  output plus simulated visual scores standing in for the real LLM call: happy-path matches (all 4
  real golden matched cases, including exact resize blocks), a forced disagreement (correctly
  downgrades to `uncertain`, still carries a best-guess `matched_asset_id`), a forced thin margin
  (also downgrades to `uncertain`), all-low-visual (`missing`, null id), and an empty candidate
  list (`missing`, doesn't throw) - all passed. What's NOT validated: whether these specific
  threshold VALUES produce good FAR/auto-accept/missing-recall numbers on real visual scores -
  that's T2.6 (score_gate2.py) + T2.7's job once the API key exists.

T2.4 (`structural-signal.ts`) implementation notes:
- Three weighted components: name (element id+type vs. candidate id/path/role, plain Jaccard),
  instance/prefab correspondence, description (element visual_description+text_content vs. the
  candidate's FULL text identity - id/path/role AND description together, not description-only -
  see the code comment on why: catalog descriptions are empty until T1.7 runs, and a color word
  like "green" usually only shows up in the element's own description, matched against a catalog
  id like `button_green` rather than another description).
- Extracted `tokenize()`/`jaccardSimilarity()` out of candidates.ts into a new shared
  `matcher/tokenize.ts` (both files needed the identical tokenizer).
- Two real tuning findings from checking scores against `golden-elements.json`/
  `golden-matches.json` (not from review - same "render and look" / "score and check" method as
  T2.2/T2.3):
  1. The instance-to-prefab bonus, weighted equally with the other two components (~0.15-0.33),
     was actively counterproductive: it ranked the generic untinted `UIElements__ButtonFrame`
     prefab above the actually-correct `UIElements__button_green` sprite for `button_continue`,
     because the Figma element is a component instance (matching the prefab bonus) even though the
     correct real-world match is a plain sprite (the majority case in this fixture - see the code
     comment, only 1 of 5 real matched golden cases is a prefab). Dropped `WEIGHT_INSTANCE` to
     0.05; brought 4 of 5 real matched cases to rank #1 among their own `candidates()` top-10 (was
     2 of 5 before the description-vs-identity redesign, 3 of 5 after it, before this weight fix).
  2. One known remaining miss: `icon_glow` still ranks its correct match
     (`UIElements__ui_img_glow`) LAST of its own top-10 candidates - plain (non-IDF-weighted)
     Jaccard loses to the same generic-prefix collision `candidates.ts` fixed with IDF weighting
     (`icon`/`heart` tokens shared with ~20 other catalog entries). Deliberately not fixed here:
     `structuralSignal` only sees one (element, candidate) pair at a time, no whole-catalog
     context to compute IDF against, and it's one of two signals `gate.ts` (T2.5) combines - flagged
     in the code as a T2.7 tuning candidate (e.g. pass an optional catalog-wide IDF map through, or
     lean on the margin/agreement check to route this case to "uncertain" rather than a wrong
     auto-accept).
- `WEIGHT_NAME`/`WEIGHT_INSTANCE`/`WEIGHT_DESCRIPTION` are module-local constants (same treatment
  as candidates.ts's `DEFAULT_TOP_K`), not exported/tunable from outside yet. Expect T2.7 to touch
  this file's weights again once `visual-signal.ts` + `gate.ts` can be scored end-to-end together
  (a real API key is the blocker for that, see the T2.3 notes above).

T2.3 (`visual-signal.ts`) implementation notes:
- Pure-JS 9-slice renderer (user's explicit call - no Unity round-trip), `render-candidate.ts`,
  using `sharp` (new dependency). Re-renders a catalog entry's already-baked `thumbnail` (tint
  included, per R14 - not reapplied here) at an arbitrary target size, respecting `render.border`
  for `image_type: "Sliced"`. `.cache/catalog.json`'s real data is Simple/Sliced only (40/25) -
  Tiled/Filled aren't implemented (fall back to Simple's full-stretch), revisit if either shows up.
- **Real bug caught by rendering and looking, not by review**: initially converted border to
  output-pixel thickness via `border / (ppu * ppu_multiplier)`, which collapsed borders to
  sub-pixel widths and erased `UIElements__button_frame_blue`'s entire rounded frame down to a
  flat rectangle. Root cause: `canvas_reference` units are Unity Canvas units, not world units -
  the right conversion is `border * (ReferencePixelsPerUnit / ppu) * ppu_multiplier`, and RPPU
  isn't in any schema (R3's CanvasScalerConfig is hardcoded per-fixture already, same as
  normalize.ts). Hardcoded RPPU=100 (Unity's default, matches every `ppu` value actually observed
  in the catalog) - see the comment in `render-candidate.ts` for the derivation. Verify against
  `RenderMetadataProbe.cs` if this project's real CanvasScaler ever uses a non-default RPPU.
- `element-crop.ts`: crops the element's region out of `fixtures/frame-export.png` (not a
  per-element Figma image-export call - stays inside the 6-req/month quota) as the visual "ground
  truth" for comparison. Inverts normalize.ts's R3 scale factor (now exported as
  `computeScaleFactor`) to map the element's `canvas_reference`-space rect back to
  `frame-export.png`'s `source_frame` pixel space. Verified visually against 5 real golden
  elements - crops line up exactly with the described element.
- Also verified visually: `UIElements__ButtonFrameTint`'s thumbnail renders green/teal, not
  purple, despite `tint: "#7349FF"`. Confirmed this is already true of the RAW thumbnail baked by
  T1.5 (not something T2.3 introduced) and is mathematically correct multiply-blend tinting - the
  untinted `ButtonFrame` base texture is itself bright green, and multiply can only dim/shift
  channels present in the base, not introduce blue/purple that isn't there. Not a bug; flagging in
  case it looks alarming again later.
- **Superseded later in the same overall work**: `visualSignal()` originally used
  `@anthropic-ai/sdk` directly (needing a real `ANTHROPIC_API_KEY`), reasoned at the time as
  cheaper/faster than `reduce.ts`'s CLI shell-out given this signal runs per (element, candidate)
  pair, not once. User pushback (didn't want a tool with a per-run money cost) led to switching
  this to the shared `src/agent/` adapter instead - see "Agent-agnostic LLM adapter" above for the
  current design, the real bugs found actually exercising it, and real Gate 2 numbers from a live
  run. The SDK dependency has been removed from `packages/mcp-tool/package.json`.

T2.2 (`candidates.ts`) implementation note: plain Jaccard token-overlap ranked candidates by raw
shared-word count, which buried the correct match under generic prefixes shared by many catalog
entries (e.g. `icon_glow`'s correct match, `UIElements__ui_img_glow`, lost to `UIElements__icon_heart`
on the bare word "icon" - the real bug, found by running `candidates()` against the real
`.cache/catalog.json` + `fixtures/golden-elements.json`/`golden-matches.json`, not by inspection).
Switched to IDF-weighted cosine-style scoring (rarity-weighted token overlap, normalized by each
candidate's own token weight) - all 5 real `golden-matches.json` "matched" cases now land in the
default top-10 candidate set. `DEFAULT_TOP_K = 10` and the tokenizer are naive/tunable; expect
`T2.7` to revisit both once `visual-signal.ts`/`structural-signal.ts`/`gate.ts` exist and can be
scored end-to-end.

Companion artifacts (fetch via WebFetch if needed - not saved locally):
- Full system design: `https://claude.ai/code/artifact/c61dba5d-c7c7-437b-85c7-fc27b88e1009`
- Vertical-slice spec (the two gates, exact thresholds): `https://claude.ai/code/artifact/62438512-43c8-4274-a143-1cf55359c916`
- Implementation plan (day-by-day tasks): `https://claude.ai/code/artifact/266f43a5-db5b-4d8f-b449-96b1266c248b`

## The fixture

- Target project: `/Users/dungphan/Melon` (a real, separate git repo - Disney.RainbowCore-based
  game). Ask the user directly for facts about it; don't explore it with subagents (see memory).
- Figma frame: "Lose Screen" (`178:35186`) in file `bvMnEkqW7u3CHT5lGNs3lg`.
- Feature folder: `Assets/Textures/UI/UI Elements` (62 sprites) + 3 explicit extra prefabs
  (`ButtonFrame`, `ButtonFrameTint`, `Scrim` - a hand-made tinted variant covering Gate 2's
  tinted-asset requirement). 65 catalog entries total.
- `fixtures/golden-elements.json`: 10 elements (7 real + 3 synthetic: `icon_gem`, `button_share`,
  `button_upgrade` - marked with a `synthetic:` `figma_node_id` prefix, excluded from Gate 1
  scoring, used only to give Gate 2 missing-recall/tint test cases).
- `fixtures/golden-matches.json`: 10 entries (7 matched, 3 missing) covering all 4 of Gate 2's
  fixture requirements (9-sliced, tinted, differently-sized-template, missing-recall) - was 8/5/3
  before `button_x` was split into 3 children (see "Composite elements" below).

## Composite elements (session addition)

`button_x` in the golden fixture is not one Unity asset - the user (who knows the real Melon
project) confirmed the mockup's close button is actually assembled from THREE sprites stacked at
the same rect: `UIElements__button_frame_x` (frame, presumably tinted red at runtime - same
pattern as `ButtonFrame`/`ButtonFrameTint`), `UIElements__button_x` (a backing layer - exact visual
role still unconfirmed, see below), and `UIElements__icon_x` (the white X glyph). This was
discovered while chasing what first looked like a rendering bug (see the corrected/superseded notes
below) - the real issue was a labeling/architecture gap, not (only) a rendering bug.

- `element-tree.schema.json` already supports this via `children` (a child is just another full
  `element`, recursively) - no schema change needed. `golden-elements.json`'s `button_x` now has 3
  children (`button_x_frame`/`button_x_base`/`button_x_icon`), each with a `synthetic:` `figma_node_id`
  prefix (same convention as the 3 already-synthetic golden elements) so Gate 1 scoring - which
  can only ever expect what a REAL Figma tree produces - correctly excludes them; `button_x` itself
  keeps its real `178:34527` id and stays recallable for Gate 1 as a single element, unaffected.
- `golden-matches.json`'s old single `button_x` entry was replaced with 3 entries (one per child),
  matched to their real assets.
- `matcher/match.ts` now recurses into `children`: an element WITH children is treated as a pure
  grouping container (not matched directly - there's no single catalog entry for "the whole
  composite"), and only its children are matched, recursively. Verified this needed NO changes to
  `candidates()`/`structuralSignal()`/`visualSignal()`/`gate()` - they already operate on a single
  `element` and a child has the identical shape. Verified the new traversal produces the exact
  same element order as `golden-matches.json`, and that `candidates()`+`structuralSignal()` rank
  2 of the 3 new children's correct match #1 (the third, `button_x_icon` -> `icon_x`, ranks 3rd,
  still within the default top-10 pre-filter).
- **Real, unsolved gap, not fixed this session**: none of this makes `reduce.ts` (the actual
  Figma-driven reduction step) produce decomposed children on its own - it has no way to know
  "this component instance needs splitting into 3 sprites" from the Figma tree alone, since (per
  the user) the real Figma layer is a single flattened visual with no separate sub-layers to
  recurse into. The decomposition here is fixture-only (hand-authored ground truth). Making this
  work for real input needs either reduction-time knowledge of known composite templates, or a
  matcher-side fallback (e.g. "if no single catalog candidate scores well, try known decompositions")
  - worth designing deliberately next time, not bolting on reactively.
- The separate rendering-bug thread (`UIElements__button_x`'s thumbnail not matching its own raw
  source PNG) that was open when this section was first written is now **fully resolved** - see
  "Thumbnail rendering bug: RESOLVED" below.
- The separate crop-sharing thread (all 3 children getting the identical full-composite crop) that
  was open when this section was first written is now **fully resolved** - see "Composite
  crop-sharing: RESOLVED" below.

## Thumbnail rendering bug: RESOLVED (session addition)

Root cause, found the hard way across five rebuild cycles and a lot of wrong turns - recorded in
full because the wrong turns are as instructive as the fix:

**The actual bug**: `RenderedThumbnail.cs` multiplied the 9-slice border values by `scale` (the
canonicalSize/nativeSize factor) before passing them to `Graphics.DrawTexture`, on the theory
(stated explicitly in this file's own original T1.5 comment) that the border ints are
DESTINATION-space pixels. **They're not** - confirmed with an isolated synthetic-texture test
(`SmokeTest.ProbeDrawTextureBorderSemantics`, still in the codebase as a reusable diagnostic):
they're literal, UNSCALED source-texture pixel counts. Multiplying by `scale` inflated the border
past the source texture's own bounds whenever an asset needed upscaling (native size smaller than
the 256px canonical thumbnail) - `Graphics.DrawTexture` doesn't error on this, it tiles/repeats
the source, which is exactly the "duplicated/scalloped" look that kicked off this whole
investigation. It went unnoticed for years of this project's own history (and every other
already-shipped Sliced asset) because it only becomes visually *obvious* on assets with strong
color contrast between their corner/edge/middle regions - `button_x`/`button_settings` have that
contrast, `button_frame_x`/`button_red`/etc. are simple enough (nearly flat color) that the same
underlying bug was invisible.

**Wrong turns, in order** (each ruled out with real evidence, not just superseded by a later
guess):
1. Stale cache theory - ruled out by rebuilding and getting a byte-for-byte identical catalog.
2. Multi-sprite-sheet theory - ruled out by `SmokeTest.DiagnoseThumbnailArtifact` (still in the
   codebase): every affected asset has exactly 1 `Sprite` sub-asset.
3. Texture Wrap Mode theory - ruled out by the same diagnostic: wrapMode is `Clamp` (correct)
   everywhere, both broken and clean assets alike.
4. SpriteAtlas packing/trim theory - this one was PARTIALLY real (every sprite in the folder IS
   packed into one shared 4096x4096 ASTC-compressed atlas, confirmed via `Sprite.texture` instance-
   ID comparison; `button_x`/`button_settings` genuinely do get trimmed further during packing than
   their loose file size) but turned out NOT to be the cause of the visible bug - three fix
   attempts building on this theory (resolving via `Sprite.texture` + a computed UV rect;
   isolating the packed region into a standalone texture via `Graphics.Blit`; recalibrating border
   values for the trim) all failed to fix `button_x`/`button_settings`, and the third one
   additionally introduced a NEW regression (`button_frame_blue` rendering as a flat, structureless
   fill) - reverted entirely once the real cause was found. `RenderedThumbnail.cs` is back to
   simply loading the raw `Texture2D` by asset path, same as its original design.

**The fix, once found, needed one more real correction**: removing the `* scale` multiplication
alone broke `UIElements__ButtonFrameTint`/`ButtonFrame` (native 531x232, needing significant
*downscale* to fit the 256px canonical size) - their unscaled border sum (147+82=229px vertically)
exceeds the ~112px-tall destRect those assets actually get downscaled into, and
`Graphics.DrawTexture` doesn't clamp this either. Added an explicit proportional clamp (shrink
`b0+b2`/`b1+b3` to fit `destRect.width`/`.height` when they'd otherwise exceed it) mirroring how
real Unity UI Images degrade a Sliced sprite that's smaller than its own border, and how
`render-candidate.ts`'s `clampBorder` already handles the identical problem on the Node side.

**Verified**: rebuilt the catalog and read back every image at each step (not just the two
originally-broken assets) - `button_x`/`button_settings` now render as clean single shapes
matching their raw source art; `button_frame_blue`/`button_red`/`button_frame_x`/`button_green`/
`icon_heart`/`ui_popup_frame` (previously-working assets, spanning Simple and Sliced,
upscale/no-scale) show no regression; `ButtonFrameTint`/`ButtonFrame` (the downscale case) now
render as complete, correct pill shapes instead of a cut-off dome. Built a full contact sheet of
all 25 Sliced catalog entries (native sizes ranging 16x16 to 1136x1300 - the widest stress range
available in this fixture) and visually reviewed every one: zero remaining duplication artifacts.
Three assets (`ui_backer_lightblue`, `ui_bar_flat`, `ui_box_rounded`) render as plain white -
plausibly legitimate untinted/placeholder assets (same pattern as `ButtonFrame`'s own untinted
base), not investigated further since none show the bug's actual symptom (duplication) - worth a
quick look if they come up again, but not currently believed to be related to this bug.

Both diagnostic methods added this session (`SmokeTest.DiagnoseThumbnailArtifact`,
`SmokeTest.ProbeDrawTextureBorderSemantics`) were left in the codebase as reusable tools, matching
this file's existing convention (`DumpImageStates`) - useful if a similar rendering mismatch shows
up on a different asset later.

**Not done, worth a follow-up rebuild before the next Gate 2 tuning pass**: run
`scripts/build-catalog.sh` one more time and re-run `score_gate2.py` against a real `topK=10`
matcher run now that the catalog's actual thumbnails are correct - the topK=3 numbers recorded
earlier in this file predate this fix entirely and should not be used to judge threshold tuning.
(Superseded by the section below - this WAS done, immediately after, in the same overall session.)

## Thumbnail rendering bug, part 2: the border-clamp fix above was still wrong (graduation-phase session)

The "RESOLVED" verification above was real but incomplete - never re-checked against
`ButtonFrame`/`ButtonFrameTint` specifically. User-reported symptom: both thumbnails visually
split into two mismatched halves along a horizontal center line ("a boat on the bottom, a smaller
upside-down boat on top"), and the corner radius, once the shape was fixed, was still roughly 2x
too large compared to a real Editor screenshot the user supplied.

**Root cause of the split**: `Graphics.DrawTexture`'s border ints are literal, unscaled pixel
counts used identically for both source-sampling width and destination-draw width - shrinking the
border pixel count (the prior session's clamp) to fit a too-small destRect **crops** the corner
artwork mid-curve instead of scaling it. `ButtonFrame`/`ButtonFrameTint` resolve to
`border=[left:90, bottom:147, right:83, top:82]` on a 256x256 source, native size 531.4x232.1 -> a
256x111.8 destRect at canonicalSize=256 - vertical border sum 229px against a 111.8px-tall
destRect, clamped to ~72/~40, cropping the bottom corner's sampled region to less than half its
real curve.

**Two dead-end fix attempts, in order** (each ruled out with real evidence):
1. Real `Canvas`/`Camera`/`UnityEngine.UI.Image` render (isolating a single synthetic `Image`
   instead of instantiating the whole prefab) - shape came out correct-looking at first glance but
   the corner radius was roughly 2x too large vs. a real Editor screenshot the user supplied.
   Root cause: `Image` border thickness is an *absolute* pixel value (from
   `spritePixelsPerUnit`/canvas `referencePixelsPerUnit`/`ppuMultiplier`), independent of the
   `RectTransform`'s own size - shrinking the `RectTransform` to fit the canonical frame (as a
   first fix attempt did) leaves the border unchanged, so it eats a much bigger fraction of the
   now-smaller rect. Compensating by zooming the camera out instead (keeping the `Image` at native
   size) hit a *second*, separate wall: `NodeBuilder.RenderSlicedToTexture`'s own comment records
   that `UnityEngine.UI.Image`'s *built-in* Sliced mesh generation is independently broken for
   these specific sprites (Tight mesh type + packed SpriteAtlasV2) once resized away from native
   size - a real, previously-confirmed bug in Image itself, not something a camera trick can route
   around. Abandoned entirely; real `UI.Image` Sliced rendering cannot be trusted for this
   project's assets at all.
2. `Graphics.DrawTexture` with the border clamp simply removed (unclamped, matching
   `NodeBuilder.RenderSlicedToTexture`'s own convention exactly) - still rendered corrupted/cropped
   (only one corner visible, opposite edges flat) for `ButtonFrame`/`ButtonFrameTint` specifically.
   Root cause: `RenderSlicedToTexture` is only ever called by the real assembler with target sizes
   close to the sprite's own proportions (Figma-matched rects), so it never actually exercises the
   border-sum-exceeds-target-size case - unclamped `DrawTexture` genuinely cannot render correctly
   once a border's sum on an axis exceeds the destination size on that axis, confirmed empirically
   here (not fixed by clamping OR not clamping - the destination is fundamentally too small for
   literal, unscaled border pixels regardless).

**The actual fix**: border pixel counts are only valid relative to the sprite's own *native* pixel
size (that's what they were authored against in the Sprite Editor) - so never composite a Sliced
sprite at any size smaller than native. When downscaling (`scale < 1`), composite once at native
size first (`CompositeAtSize`, unclamped `Graphics.DrawTexture`, same call `RenderSlicedToTexture`
already uses successfully) - guaranteed non-degenerate, since the border was designed against
exactly that size - then bilinear-resample the *finished bitmap* down to the target size
(`ResampleBilinear`, a plain `Graphics.Blit`, no border logic at all) before centering it into the
canonical square canvas. Upscale/no-scale (`scale >= 1`) still composites directly at the target
size, unchanged from the already-verified upscale behavior above.

**Verified**: rebuilt the catalog; `ButtonFrame`/`ButtonFrameTint` now render as clean rounded
rectangles with a correct, modest corner radius matching the user's real-Editor screenshot, and a
tint color the user confirmed looks right. Spot-checked `button_frame_blue`/`icon_heart` (no
regression) plus the two most extreme downscale cases in the whole catalog,
`playcontainer_rectangle` (scale 0.197) and `ui_gamescreen_countertop` (scale 0.25) - both render
as clean single shapes, no seams. **Not done**: a full contact-sheet re-review of all 65 catalog
entries (two previous sessions each claimed this kind of verification and still missed a real bug)
- worth doing before trusting this for Gate 2 tuning again. Diagnostic additions this session, left
in `SmokeTest.cs` per this file's usual convention: `DumpButtonFrameMetadata` (border/native/scale
math dump), `DumpButtonFrameShaderAndTexture` (material/shader/texture-identity dump).

## Thumbnail rendering bug, part 3: prefab thumbnails only showed the "main image" (same session)

Follow-up user report, same conversation: `ButtonFrame`'s thumbnail (now shape/color-correct)
still only showed the green button - missing the larger blue frame Image behind it, a white
icon-placeholder Image, and a "100" TMP label, all of which are real, active parts of the prefab
and visible in a real Editor screenshot. Root cause: `RenderedThumbnail.cs`'s prefab path only ever
rendered `RenderMetadataProbe.ResolveMainImage`'s single result - correct for *matching* (scoring
needs one representative image) but not for a *thumbnail*, which should show the whole asset.

**Fix**: added `RenderPrefabHierarchy`, which instantiates the prefab for real
(`PrefabUtility.InstantiatePrefab`, same as `NodeBuilder.BuildFromPrefab`) and renders every
active/enabled/visible `Image` and `Text`/TMP in it through a real Canvas + Camera, so z-order,
relative positions, and text all come from Unity's own UI layout rather than being
reimplemented. Each contributing *Sliced* `Image`'s sprite is swapped for a pre-composited Simple
one at its own authored rect size first (same `CompositeAtSize` from part 2, same swap-before-
render recipe `NodeBuilder.ResizeMainImageToFill` already uses for the single-image case) - Simple
Images and Text are left alone and render natively, since only `UI.Image`'s Sliced path is
confirmed broken for this project's assets.

**Real bug found while building this**: the prefab root's overall size (used to size the camera/
canvas around the whole composite) was read from `RectTransform.rect` *after* forcing the root's
anchors to a fixed center point for framing purposes - for a stretch-anchored root (`Scrim`,
designed to fill whatever screen it's placed in - `anchorMin != anchorMax`), flipping to fixed-
point anchors changes what `rect.size` even means (it becomes `sizeDelta` directly, discarding
whatever size the stretch used to resolve to). This wasn't merely "sometimes reads as 0" - the
initial stretch-resolved read against our synthetic canvas's own arbitrary default 100x100
`RectTransform` size looked like a plausible, non-zero, "native" size (100x100) and silently passed
a naive `width < 1` degeneracy check, while `Scrim`'s *real* authored size is 512x512 - so `Scrim`
rendered as a fully transparent 100x100 image (correct data, wrong scale) rather than anything
obviously broken. Fixed by detecting stretch directly from the anchors (`anchorMin != anchorMax`)
rather than inferring it from the resolved rect, falling back to the already-resolved
`RenderMetadataProbe` `NativeWidth`/`NativeHeight` (matching `ProbePrefab`'s own documented
fallback for this exact case) whenever the root was stretch-anchored, and always explicitly
re-assigning `sizeDelta` after the anchor change rather than only in the (unreliable) "degenerate"
branch.

**Verified**: rebuilt the catalog; `ButtonFrame`/`ButtonFrameTint` thumbnails now show the full
composite (frame + button + icon placeholder + "100" label) matching a real Editor screenshot.
`Scrim` (the stretch-anchored case) now renders as a solid, correctly-sized, correctly-tinted
512x512 fill (confirmed via direct pixel sampling, not just visual inspection - a Read-tool preview
of a transparent PNG can look identical to "nothing rendered"). Spot-checked
`button_frame_blue`/`icon_heart` (bare sprites, unaffected by the prefab-path change) - no
regression. `RenderMetadataProbe.ResolveMainImage` is untouched and still used for matching/
scoring - this only changes what the *thumbnail* renders, not what candidate matching scores
against.

## Thumbnail rendering bug, part 4: Layout Group children never got a layout pass (same session)

Immediate follow-up: `ButtonFrame`'s icon placeholder rendered centered on/behind the "100" label
instead of beside it, while `ButtonFrameTint` - built from the identical component setup - rendered
correctly. User correctly guessed the mechanism unprompted: a Layout Group needing a forced update.

Confirmed via a new diagnostic (`SmokeTest.DumpLayoutComponents`): both prefabs have the exact same
`HorizontalLayoutGroup` (on `ButtonContinue`) + `ContentSizeFitter` (on the TMP label) +
`LayoutElement` (on the inactive-state sibling) - so the bug isn't a structural difference between
the two prefabs. Root cause: a Layout Group only repositions its children on an actual layout pass,
which normally runs lazily (next UI update / next time something marks it dirty), not synchronously
on `PrefabUtility.InstantiatePrefab`. Absent a forced pass, whatever `anchoredPosition` happens to
already be serialized in the prefab file is what renders - `ButtonFrameTint`'s serialized icon
position happened to already match a rebuilt layout (last saved in the Editor after a layout pass
ran), `ButtonFrame`'s didn't (serialized from before the layout group was added, or before its last
edit ran one) - coincidence, not a real difference in correctness between the two assets.

**Fix**: one line, `LayoutRebuilder.ForceRebuildLayoutImmediate(rootRt)` in `RenderPrefabHierarchy`,
called after the Sliced-sprite swap and before the camera/canvas render - recursively rebuilds every
nested Layout Group/ContentSizeFitter under the prefab root regardless of what was serialized.

**Verified**: rebuilt the catalog; `ButtonFrame`/`ButtonFrameTint` now render identically (icon and
"100" label side by side in both). Spot-checked `Scrim` (pixel-sampled, still correct navy fill),
`button_frame_blue`/`icon_heart` (bare sprites, no Layout Group, unaffected) - no regression.
Diagnostic addition this session: `SmokeTest.DumpLayoutComponents`.

## Full contact-sheet review of all 65 catalog entries: DONE (same session)

Parts 2/3 above each said "not done" for this - actually done now, after part 4's fix. Built two
labeled contact-sheet PNGs (all 65 thumbnails, sorted by id) directly from `catalog.json`'s baked
`thumbnail` fields and reviewed both visually: zero seam/boat-split artifacts, zero corner
distortion, every rounded shape a single clean silhouette. Also ran a systematic per-entry alpha-
coverage check (not just visual spot-checks) - zero entries render as accidentally-fully-transparent
(the `Scrim`-style "looks blank but is actually correct low-alpha data" trap from part 3 is fully
gone; this check would have caught a repeat). 4 entries are fully opaque edge-to-edge by design, not
bugs: `Scrim` (a full-screen dimming overlay), `ui_bar_flat`/`ui_box`/`ui_string` (plain rectangular
backer/placeholder art - consistent with the "plausibly legitimate untinted placeholder" note from
the original "Thumbnail rendering bug: RESOLVED" session). Confidence in the catalog is now real,
not just claimed - safe to move on to Gate 2 tuning against it.

## Composite crop-sharing: RESOLVED (session addition)

The gap flagged in "What's not done yet" as unfixed: `button_x_frame`/`button_x_base`/
`button_x_icon` all share one rect, so `cropElementFromFrame` returned the identical
full-composite crop (all 3 layers visible at once) for every one of them, while each was being
compared against only ITS OWN isolated candidate render - a structural mismatch (whole picture vs.
one of its layers) baked into every candidate's score regardless of correctness.

**Fix**: `packages/mcp-tool/src/matcher/composite-visual-signal.ts` (new file) - `match.ts`'s
traversal now detects a "composite group" (a parent whose children all share the same rect, within
a small epsilon) via `isCompositeGroup`/`collectWorkItems`, and routes those through
`compositeVisualSignals` instead of the normal per-element `visualSignal` path. `structuralSignal`
still runs per-child independently as before (text-based, never had this problem). Visual scoring
is now genuinely joint: candidate renders from ALL of a group's children are composited together
(stacked, children-array order = back-to-front - `button_x`'s children already happen to be listed
frame/base/icon, icon-on-top being the only order that looks right) and the WHOLE composite is
compared against the shared crop, not any one child's render alone.
- Algorithm: coordinate-ascent / Gauss-Seidel local search, not exhaustive search over every
  candidate combination (topK^childCount would be 10^3 for this one fixture group alone, and only
  gets worse with more children or a larger topK). Each child's "current pick" is initialized from
  its own structuralSignal top choice (unaffected by the crop-sharing bug), then for a couple of
  passes, each child in turn is rescored across all of its own topK candidates - holding every
  OTHER child's current pick fixed, compositing each candidate into that child's slot, and scoring
  the resulting whole composite against the shared crop - then moves its pick to whichever scored
  best, visible immediately to later children in the same pass. The last pass's per-candidate
  scores become that child's visual-signal array, fed into `gate.ts` exactly like a normal
  per-element `visualSignal()` result would be - no changes needed to `gate.ts` itself.
- `compareImages` was factored out of `visual-signal.ts` (previously inlined in `visualSignal`) so
  the joint composite-vs-crop comparison reuses the exact same SSIM+histogram scoring rather than a
  second copy of it.
- **Verified real improvement, not just "runs without crashing"**: reran `ui-assembler match` at
  topK=10 against the real fixed catalog and rescored with `score_gate2.py`. `button_x_base` went
  from `missing` with an implicitly-wrong match (the pre-fix state HANDOFF documented) to
  `uncertain` with the CORRECT asset id (`UIElements__button_x`) - the matcher now genuinely
  identifies the right asset, just doesn't clear the margin threshold for a confident `matched`.
  `button_x_icon` also lands on its correct asset id (`UIElements__icon_x`), still `uncertain`.
  `button_x_frame` still picks the wrong asset (`UIElements__button_x` instead of the correct
  `UIElements__button_frame_x`) - but this was checked by actually rendering and looking at both
  composites side by side (not assumed): the crop and BOTH candidate composites are correctly
  aligned/shaped/centered (compositing and cropping are doing their job right), and
  `UIElements__button_frame_x` vs. `UIElements__button_x` are themselves two genuinely
  near-identical plain red rounded frames to the human eye once composited - the coarse pixel-based
  visual signal (SSIM + color histogram, see "Pure-JS visual signal" below) simply can't discriminate
  between two visually-near-identical same-color competitors, the same class of limitation already
  documented for `icon_glow` vs. `icon_heart`. Not a bug in this fix; a pre-existing, separate,
  already-known coarseness of the visual signal surfacing on a new pair. Gate 2's overall headline
  numbers (FAR/auto-accept/missing-recall) are unchanged by this fix, as expected - they're
  currently dominated by the OTHER known gap (synthetic elements' crops not corresponding to real
  content) - see "Synthetic-element crop quality: RESOLVED" immediately below, fixed in the same
  session as a follow-up.

## Synthetic-element crop quality: RESOLVED (session addition)

The other gap flagged in "What's not done yet" as unfixed, and the one actually driving Gate 2's
real numbers (unlike the composite-crop-sharing fix above, whose effect on the headline metrics was
expected to be a wash): `icon_gem`/`button_share`/`button_upgrade` are 3 top-level golden elements
fabricated for Gate 2 test coverage (missing-recall x2, tint-matching x1) with made-up `rect`s that
don't correspond to any real content in `fixtures/frame-export.png` - `cropElementFromFrame` was
cropping real-but-irrelevant mockup pixels (e.g. `button_upgrade`'s crop was a screenshot of a
"2:35" HUD timer) and feeding that into `visualSignal` as if it were meaningful ground truth. Real,
confirmed impact on the last real topK=10 run: `button_upgrade` false-accepted
`UIElements__button_frame_blue` instead of the intended `UIElements__ButtonFrameTint`, and
`button_share` came back non-`missing` when golden wants `missing`.

**Fix**: `element-crop.ts`'s new `resolveElementCrop` - for an element whose OWN `figma_node_id`
carries the `synthetic:` prefix, load a hand-authored `fixtures/synthetic/<element.id>.png`
instead of cropping the real frame export; every other element (including `button_x`'s composite
children, which also carry a `synthetic:` prefix but whose SHARED rect is real, decomposed content
- see "Composite elements" above) is unaffected, since `match.ts`'s composite path calls
`cropElementFromFrame` directly rather than going through this resolver. `visualSignal` now calls
`resolveElementCrop` instead of `cropElementFromFrame` directly; `compareImages` (already factored
out for the composite fix) is reused unchanged.
- **The 3 reference images aren't arbitrary placeholder art** - each was built to be an honest,
  non-circular ground truth for what the fixture's own `visual_description` already claims:
  - `icon_gem`/`button_share` (need to score LOW against every real candidate, by design - no
    matching asset exists) are hand-drawn SVGs rendered via `sharp`, colored to plausibly match
    their description ("purple gem/diamond currency icon", "blue share/social button" via a
    circles-and-lines share glyph).
  - `button_upgrade` (needs to score HIGH specifically against `UIElements__ButtonFrameTint`) is
    NOT hand-drawn - it's `renderCandidateAtSize(UIElements__ButtonFrame, ...)` (the REAL, different,
    UNTINTED catalog entry) multiply-blended by `#7349FF` in a one-off generation script. This
    isn't leaking the answer via a circular self-comparison: the base geometry/shading comes from a
    different catalog entry than the one being tested for, and the tint hex was already public
    information in the fixture's own human-authored `visual_description` ("tint-aware matching
    against a real tinted catalog asset ... tint #7349FF"). It's simulating exactly what a mockup
    artist's Figma layer would show if they'd applied that documented tint to that documented base
    asset - the same multiply-blend math `RenderedThumbnail.cs` itself applies server-side.
- **Real, non-obvious pitfall hit and fixed while hand-authoring the two "should score low" SVGs**:
  early attempts (diamond/pentagon icon shapes with sharp points) left ~30-35% of the 120x120 canvas
  transparent at the corners - `toComparableImage`'s gray-flatten treatment then turned that into a
  huge uniform mid-gray region that coincidentally overlapped heavily with several real candidates'
  own mid-tone pixels in the (coarse, only-8-bins-per-channel) color histogram, adding ~0.25-0.30 to
  the score almost regardless of hue choice - found by dumping the actual per-channel histogram
  vectors and noticing a suspicious shared spike at exactly bin 4 (the 128-159 range, i.e. the flatten
  gray) on BOTH sides. Real candidate renders never have this problem (`render-candidate.ts` always
  crops to content-bbox then stretches to fill the FULL target canvas, so they're edge-to-edge
  opaque) - fixed by giving both synthetic images a fully opaque background (no transparency at
  all), which alone dropped `icon_gem`'s worst-case score from ~0.28 to ~0.14-0.21 before any further
  color tuning. Worth remembering for any future hand-authored fixture image: match real renders'
  edge-to-edge opacity, don't leave transparent margins the comparison pipeline will silently
  convert into an artificial similarity signal.
- **Verified with a real topK=10 run**, not just the isolated per-candidate scores used while
  iterating the art: reran `ui-assembler match` + `score_gate2.py` against the real fixed catalog.
  All 3 synthetic elements now land exactly on golden's expected status (`icon_gem`/`button_share`
  `missing`, `button_upgrade` `matched` to `UIElements__ButtonFrameTint`) with no regression on any
  of the other 7 golden elements. Gate 2 headline numbers: FAR 33.3% -> **0.0%**, auto-accept
  unchanged at 42.9% (not driven by this gap - see "Composite crop-sharing" above and the residual
  `icon_glow`/`button_x_frame` limitations), missing-recall 66.7% -> **100.0%**. Verdict moved from
  **FAIL to SOFT PASS** (auto-accept rate is the only metric still below its PASS bar, 42.9% vs.
  50%).

## Pure-JS visual signal + gate redesign (session addition)

**Why this happened**: asked to rerun the topK=10 matcher against the fixed catalog and score it.
It failed partway through - not a bug, a real constraint: `visualSignal` at the time (the
`claude`-CLI agent adapter, subscription-auth, no separate API key - see "Agent-agnostic LLM
adapter" above) makes one nested `claude` invocation per (element, candidate) pair; at topK=10
across 10 matchable elements that's up to ~100 invocations, and the user reported this exhausted a
real Claude subscription's usage quota mid-run. Subscription auth avoids a metered BILL but not a
USAGE LIMIT - the two are different constraints, and this signal needed to be free to run
unboundedly, not just cheap. User's call, after discussing weight/dependency tradeoffs (ruled out a
Python/scikit-image or OpenCV route - would add a third language runtime + real install friction
for one signal, no capability this task actually needs that pure JS lacks): rewrite `visualSignal`
as pure local pixel math, no LLM/network call at all.

- **`visual-signal.ts` rewritten**: crops + candidate render (unchanged - `element-crop.ts`,
  `render-candidate.ts`) are now compared via SSIM (`ssim.js`, new dependency, pure JS, zero
  transitive deps - structural/luminance similarity, catches wrong template/shape) combined with a
  color histogram distance (hand-rolled, no new dependency - catches wrong tint/color, which SSIM
  alone tends to miss since it's mostly luminance-based). Both images flattened onto a matching
  neutral gray background before comparing (crop has no real transparency, being a flat-screenshot
  slice; render can have real transparent padding - flattening both the same way keeps the
  comparison symmetric instead of letting the render's transparent-pixel color convention bias the
  histogram). Runs in milliseconds; a 70-comparison validation run that would have taken minutes
  and ~70 quota-consuming calls under the old approach took under 2 seconds.
- **`@anthropic-ai/sdk`-free and now `agent`-free for this signal specifically**: `reduce.ts` still
  uses the shared `src/agent/` adapter (one call per pipeline run - fine, that's what subscription
  reuse was actually suited for). `VisualSignalOptions` dropped its `agent` field accordingly.
- **Validated the new signal is directionally sound but coarser than the LLM-based one it
  replaced** (expected, and the accepted tradeoff of going fully local): checked all 7 real matched
  golden elements' visual scores across their own real `candidates()` top-10 pools - the correct
  asset never ranked last, but only ranked #1 by visual score ALONE in 2 of 7 cases (worse than the
  old approach, though the old approach's real accuracy against a large real candidate pool was
  never actually measured before it started exhausting quota).
- **`gate.ts` redesigned as a direct consequence**: it previously selected "best candidate" by raw
  visual score alone, using structural score only for the `agree` cross-check. With visual alone
  picking wrong 5 of 7 times, that design silently inherited visual's mistakes. Candidate SELECTION
  now ranks by a combined score - each signal min-max NORMALIZED across this element's own
  candidate set (makes visual's pixel-metric scale and structural's token-overlap scale
  commensurable without needing to know either's "true" global range), weighted 0.6 visual / 0.4
  structural. `agree` redefined to still be a meaningful cross-check post-combination: does EITHER
  signal's own independent top pick match the combined winner (a strict "both signals' own #1 must
  match the combined pick" almost never fires once selection blends the two - the combined winner
  is often each signal's own runner-up by construction). Absolute accept/reject thresholds
  (`MATCH_VISUAL_THRESHOLD`/`MISSING_VISUAL_THRESHOLD`) stay on the RAW visual score, deliberately
  NOT the normalized one - normalizing per-element always stretches the best of a candidate set
  toward 1.0 regardless of absolute quality, which would make even a genuinely-missing element's
  best (also-bad) candidate look artificially confident. `margin` uses the normalized/combined
  score instead (inherently a relative "how much better than the runner-up" question, unlike the
  absolute accept/reject decision).
- **Thresholds retuned against this real data** (`MATCH_VISUAL_THRESHOLD=0.4`,
  `MISSING_VISUAL_THRESHOLD=0.24`, `MARGIN_THRESHOLD=0.12`, replacing placeholder guesses that
  assumed an LLM-like 0.9+-for-confident-matches scale the pixel metric never reaches - the best
  real match observed, `icon_heart`, tops out at 0.68 raw visual).
- **Real topK=10 Gate 2 readout** (`.cache/match-result.json`, scored via `score_gate2.py`): FAR
  33.3%, auto-accept 42.9%, missing-recall 66.7%, verdict FAIL. Read this with the same care as
  every other number in this file - the failure is now well-diagnosed, not mysterious:
  - Of the 10 matchable elements, only 4 are "clean" (unaffected by an already-documented
    limitation): `Scrim`, `icon_glow`, `icon_heart`, `button_continue`. Of those 4:
    `icon_heart`/`button_continue` are correctly `matched`; `icon_glow` is the same
    already-documented `icon_heart`-collision ambiguity from T2.4 (still unresolved, same root
    cause: a coarse metric losing to a closely-related, wrong sibling asset); `Scrim` correctly
    lands on `missing` after the threshold retune (was landing right at the boundary, 0.236 vs.
    the old 0.22 cutoff).
  - The one real false-accept (`button_upgrade` -> `button_frame_blue` instead of
    `ButtonFrameTint`) and the one missed-recall (`button_share`) are BOTH synthetic elements whose
    crops are already documented (below) as not corresponding to real content - not a fresh
    finding, the same known gap surfacing through the new signal.
  - The 3 composite children (`button_x_frame`/`button_x_base`/`button_x_icon`) underperform for
    the already-documented reason below (composite children sharing one crop) - `button_x_icon`
    landed `uncertain` (close), `button_x_frame` `uncertain`, `button_x_base` `missing` (wrong -
    should be `matched`).
  - **Bottom line**: the matcher's own logic is behaving reasonably on every case not already
    compromised by one of two known, separate, unfixed data-quality gaps. Fixing either of those
    (see "What's not done yet") is very likely to move the real numbers more than further threshold
    tuning would at this point.

## Automated screenshot renderer (session addition)

Closes the gap flagged just above ("Still not done": a batch-mode screenshot renderer) - the only
remaining verification method for the assembled prefab was the user's own manual Editor
screenshot, not a repeatable artifact. New file
`packages/unity-editor/Editor/Batch/RunScreenshot.cs` + `scripts/screenshot.sh`.

- Builds the exact same in-memory hierarchy `RunAssemble.cs` builds - `CanvasScaffold` +
  `NodeBuilder`, reused directly rather than re-implemented, so this renders exactly what the real
  assembler produces - but never saves it as a prefab asset; it's a throwaway scene object,
  destroyed after the screenshot is captured.
- The render recipe (`ScreenSpaceCamera` Canvas + an orthographic camera targeting a
  `RenderTexture` + `Canvas.ForceUpdateCanvases()` before a synchronous `Camera.Render()`) is not
  new - it's the exact one `SmokeTest.CaptureCatalogEntryRender` already validated for single
  catalog entries (see "Thumbnail rendering bug" investigation above). What's new is applying it to
  the FULL `CanvasScaffold`-built hierarchy instead of one sprite - `CanvasScaffold` always creates
  a `ScreenSpaceOverlay` Canvas (correct for the real saved prefab), so `RunScreenshot` mutates
  `renderMode`/`worldCamera` on the returned Canvas afterward, screenshot-only; `CanvasScaffold`
  itself is untouched.
- Renders at exactly `canvas_reference` resolution, not an arbitrary device size - deliberate:
  `NodeBuilder` positions every element via absolute `anchoredPosition`/`sizeDelta` already in
  `canvas_reference`-space units, which only maps 1:1 to on-screen pixels when `CanvasScaler`'s
  computed scale factor is exactly 1 - true when the render target's size equals
  `referenceResolution` exactly (`computeScaleFactor`'s log2 blend collapses to log2(1)=0 on both
  axes). Orthographic camera with `orthographicSize = h/2` makes 1 world unit = 1 render-target
  pixel regardless of `planeDistance` (no perspective divide) - same relationship
  `CaptureCatalogEntryRender` already relies on.
- Background is transparent by default (`-bgColor` overrides) rather than guessing a fill color -
  no scrim/background element is a matchable asset in this fixture's catalog, so a transparent
  render composites cleanly for a side-by-side or overlay diff without assuming one.
- **Verified end-to-end on the first real run** against the live Melon project (Unity Editor was
  not already open on it - checked via `ps aux` first, since batch mode fails fast exit 134
  otherwise): `scripts/screenshot.sh` produced `.cache/rendered-frame.png` (1206×2622, matching
  `canvas_reference` exactly) with every expected element present and correctly placed/sized (top
  HUD bar, title/subtitle text, heart icon, close X, bottom continue button) - matches the earlier
  user-captured Editor screenshot element-for-element. `NodeBuilder`'s own skip-`Debug.LogWarning`s
  fired correctly for `Scrim`/`icon_gem`/`button_share` (`missing`) and `icon_glow` (`uncertain`) -
  this run used `.cache/match-result.json` as-is (raw matcher output, pre-human-review promotions),
  not the promoted state `scoring/report.md`'s Week 3 section describes; re-run against a
  promoted/reviewed match-result.json for a render matching the fully-`matched` prefab. Saved a copy
  as `fixtures/rendered-frame.png` (checked in, unlike `.cache/`) and added a new "Automated
  screenshot renderer" section to `scoring/report.md` with the side-by-side.
- Not yet built: an actual pixel-diff/SSIM score between this render and `frame-export.png` (this
  session only proved the renderer itself works and produces a visually-correct artifact) - would
  need to handle the known, out-of-scope gaps first (no TMP font/color styling data in
  `element-tree.json`, `Scrim`/gem sprites correctly absent per this run's match results) or the
  diff would be dominated by things that aren't rendering bugs.

## Week 3: the assembler (session addition)

Implemented T3.1-T3.5 (`CanvasScaffold.cs`, `NodeBuilder.cs`, `PrefabWriter.cs`,
`RunAssemble.cs`, `scripts/assemble.sh`), extending the pre-existing stub files
(scaffolded in an earlier session, all throwing `NotImplementedException`).
Two deliberate, user-confirmed design decisions before coding:
- `CanvasScaffold` reads `canvas_reference`/`canvas_match_mode`/
  `canvas_match_value` **from `element-tree.json`** (already written there by
  `normalize.ts`/T1.10, itself hardcoded to match the project's real
  CanvasScaler setting) - not a second, independent live query into an
  existing Melon Canvas asset.
- Default assembled-prefab output path: `Assets/_Generated/UIAssembler/<sanitized-frame_id>.prefab`.

**New file `Editor/Assembler/AssemblerJson.cs`**: a hand-rolled recursive-
descent JSON parser + typed loaders for `element-tree.json`/`match-result.json`/
`catalog.json`. Confirmed no Newtonsoft Json.NET in Melon's `Packages/manifest.json`,
so this continues `RunCatalogBuild.cs`'s existing precedent (hand-rolled JSON,
since `JsonUtility` can't parse a top-level array or nullable fields) rather
than adding a new Unity package dependency. Data classes intentionally carry
only what `NodeBuilder` needs (not every schema field) - border/PPU/native_size/
thumbnail are irrelevant to assembly since Unity reads 9-slice border straight
off the assigned `Sprite` asset itself.

**`NodeBuilder.cs`'s key design call**: flat instantiation - every leaf element
(whether top-level or a composite's child) becomes a **direct sibling** under
the root Canvas, positioned by its own absolute `canvas_reference`-space rect
(top-left anchor/pivot, `anchoredPosition=(x,-y)`, `sizeDelta=(w,h)` - no
relative-position math needed). Grouping containers (elements with non-empty
`children`, e.g. `button_x`) are walked for traversal only, never instantiated
themselves - same convention `matcher/match.ts` already established.
Element-tree array order is preserved depth-first when appending under the
Canvas, which is already established (see "Composite crop-sharing: RESOLVED"
above) to be back-to-front, so Unity's sibling-index draw order comes out
correct with no extra z-ordering logic. Only `match-result.json` entries with
`status == "matched"` get built; `"uncertain"`/`"missing"` are skipped with a
`Debug.LogWarning` (matches the stub doc comment's "confirmed match-result.json"
wording and the Gate 2 report's "mandatory review on uncertain" recommendation).
`type == "text"` elements never appear in `match-result.json` (the matcher
already filters them out) - built directly as `TextMeshProUGUI` from
`text_content`, with TMP autosizing defaults since element-tree carries no
structured font/color data (text-fit is an explicitly deferred refinement).
Needed adding a `"Unity.TextMeshPro"` asmdef reference (Melon's manifest
already has `com.unity.textmeshpro`; the assembly reference just wasn't wired
up).

**Two real compile bugs found by actually running this against the real Unity
project** (not caught by review):
1. `RunAssemble.cs` used `Dictionary<string,string>.GetValueOrDefault` (an
   extension method needing `using System.Collections.Generic;` in scope)
   without that using directive, having only fully-qualified the parameter
   type in one place - `RunCatalogBuild.cs` already used the same call
   correctly because it already had that using. Fixed by adding the missing
   `using`.
2. `scripts/assemble.sh`'s conditional `"${EXTRA_ARGS[@]}"` expansion of a
   zero-element array under `set -u` on macOS's bundled bash 3.2 throws
   "unbound variable" (fixed in bash 4.4+, not before) - fixed with the
   `"${EXTRA_ARGS[@]+"${EXTRA_ARGS[@]}"}"` idiom.

**Verified end-to-end against the real fixture stand-ins** (`fixtures/golden-elements.json`
as `element-tree.json`, the real `.cache/match-result.json` + `.cache/catalog.json`
from Week 2's topK=10 run): `scripts/assemble.sh` ran clean against the real
Melon project and wrote `Assets/_Generated/UIAssembler/178_35186.prefab`.
Skipped (logged) exactly the 7 elements with a non-`matched` status - `Scrim`,
`icon_glow`, all 3 `button_x_*` composite children, `icon_gem`, `button_share`
- built exactly the 3 `matched` elements (`icon_heart`, `button_continue`,
`button_upgrade`) plus the 2 `text` elements (`text_title`, `text_subtitle`).
Verified by reading the saved prefab's raw YAML (not just "it ran without
error"): CanvasScaler's `m_ReferenceResolution`/`m_ScreenMatchMode`/
`m_MatchWidthOrHeight` match `canvas_reference`/`canvas_match_value` exactly;
every built element's `m_AnchoredPosition`/`m_SizeDelta` match its
`golden-elements.json` rect exactly (cross-checked `icon_heart` and
`button_upgrade` by hand - initially looked like a swapped-rect bug from a
careless ad-hoc debug script, but was a misreading of that script's own
output, not a real bug); `button_upgrade` (matched to the `ButtonFrameTint`
**prefab**, not a sprite) came through correctly as a nested `PrefabInstance`
with its `RectTransform`/name overridden via proper prefab-modification
records, not a raw copy. One real, structurally-correct-but-worth-noting
Unity behavior: `PrefabUtility.SaveAsPrefabAsset` renamed the root GameObject
from `"Canvas"` (as `CanvasScaffold` names it) to match the output filename
(`178_35186`) - standard Unity semantics (a saved prefab's root name mirrors
its asset name), not a bug.

**T3.4's live visual review, done by the user (this agent has no interactive
Editor GUI access) - found one real bug and validated the review policy.**
Two things the user reported after opening `178_35186.prefab` in the Editor:
`button_upgrade` sitting oddly at top-left (expected - it's a **synthetic**
golden element with a fabricated rect, not real mockup content, see "The
fixture" above) and no X button at all (expected by design - all 3 composite
children were `uncertain`, and `NodeBuilder` deliberately skips anything not
`matched`). But the user also confirmed `button_continue`'s matched asset was
wrong: **`golden-matches.json` itself had the wrong ground truth for this
element** (`UIElements__button_green` instead of the real correct answer,
`UIElements__ButtonFrame` - the shared untinted prefab template that
`ButtonFrameTint` is a tinted variant of, already green in its native color).
This is the same asset T2.4's `WEIGHT_INSTANCE` tuning decision deliberately
ranked *down* because it was "wrongly" beating `button_green` - that tuning
call was chasing a mislabeled golden entry, not a real matcher bug, and the
prior Gate 2 "0.0% FAR" number was never a true 0% as a result (a false accept
scoring can't see if the ground truth it diffs against is itself wrong). Fixed
`golden-matches.json`'s `button_continue` entry, manually promoted the 3
composite-child `uncertain` entries in `.cache/match-result.json` to `matched`
after the user confirmed each asset directly (`button_x_frame` →
`UIElements__button_frame_x`, matching this file's own prior speculation, not
the matcher's actual `uncertain` pick of `UIElements__button_x`;
`button_x_base`/`button_x_icon` confirmed correct as-is), and re-ran
`scripts/assemble.sh` - the rebuilt prefab was verified the same way as
before (reading the saved prefab's serialized data field-by-field), now
showing `button_continue` as a `ButtonFrame` `PrefabInstance` and all 3 X
button layers present in the correct back-to-front order. Re-scored Gate 2
against the corrected golden fixture: **FAR 0%, auto-accept 85.7% (up from
42.9%), missing-recall 100%, verdict PASS** (was SOFT PASS) - see
`scoring/report.md`'s new "Update after Week 3's real prefab review" section
for the full account, including the important caveat that this PASS reflects
the golden-fixture fix + a real human review pass, **not** a matcher
improvement - the matcher's own raw output for these elements is unchanged.

~~**Still not done**: a batch-mode screenshot renderer~~ - **RESOLVED in a
later session**, see "Automated screenshot renderer" below (new
`RunScreenshot.cs` + `scripts/screenshot.sh`).

## Second review pass: a real NodeBuilder bug + a corrected prior finding (session addition)

A second round of the user's live Editor review (same rebuilt prefab) found two more real issues -
one a genuine `NodeBuilder` bug, one a correction to something this file previously stated as fact.

**Bug: prefab catalog entries with a non-flat internal hierarchy weren't being resized correctly.**
`button_continue`'s matched asset, `UIElements__ButtonFrame`, isn't a flat prefab - its root is a
decorative outer wrapper (a separate, unprobed sprite), and the actual visible button Image + label
live on an inner child (`ButtonContinue`) that's center-anchored with its own fixed size, NOT
stretch-anchored to its parent. This is the exact same GameObject
`RenderMetadataProbe.ResolveMainImage` already picks out when probing this entry's render metadata
into `catalog.json` (confirmed by reading `ButtonFrame.prefab`'s raw YAML: the Button's
`m_TargetGraphic` fileID matches the inner child's Image component exactly, not the root's own
separate Image). `NodeBuilder.BuildFromPrefab` was only resizing the ROOT's `RectTransform`
(`PositionRect`), so the inner child - the actual visible content - stayed frozen at its native
~531x232 size regardless of the element's target rect (838x285), and the user reported this as
"wrong green image size" + "no round corners" (the outer wrapper's border, unaffected by
`ppuMultiplier` per Unity's normal 9-slice behavior, looked disproportionately small/square relative
to a resized-but-visually-empty root once the real content wasn't tracking it). **Fixed**: added
`NodeBuilder.ResizeMainImageToFill`, which reuses `RenderMetadataProbe.ResolveMainImage` (same method
that built the catalog entry) to find the actual visible child, stretch-anchors it to fill its parent
(`anchorMin=(0,0)`, `anchorMax=(1,1)`, zero offsets), and applies the same `ppuMultiplier` R14
treatment the sprite path already had. Verified by re-reading the saved prefab: the inner
`ButtonContinue` child's `RectTransform` now carries stretch-anchor overrides instead of staying at
its native fixed size. **This is a real, generally-applicable gap** - any future catalog entry that's
a non-flat prefab (decorative wrapper root + a differently-anchored inner content child) would have
hit the same bug; not specific to `ButtonFrame`.

**Correction: the composite-child rects were NOT actually hand-authored ground truth - they were a
placeholder, and the real geometry was recoverable all along.** The user reported `icon_x` rendering
"bigger than the mockup" for `button_x_icon`. Investigating led to a bigger finding than expected:
this file previously stated (see "Composite elements" above) that "the real Figma layer is a single
flattened visual with no separate sub-layers to recurse into" and that the 3-way rect decomposition
was "fixture-only, hand-authored ground truth." **That was wrong.** The Figma plugin export cache
(`.cache/figma/bvMnEkqW7u3CHT5lGNs3lg/178-35186.json`, populated by the existing figma-plugin flow -
see "Non-obvious operational facts") already contains `button_x`'s full expanded sub-tree (GROUP/
RECTANGLE/INSTANCE nodes for the frame, base, and icon layers, each with real
`absoluteBoundingBox` data) - nobody had actually looked inside that cached JSON for this composite
before assuming it was flat. Derived and verified the exact coordinate transform against three
already-known-correct golden elements (`icon_heart`, `button_continue`, and the frame node itself):
`elementRect = figmaAbsoluteBoundingBox - frameOrigin` where `frameOrigin` is the frame node's own
`absoluteBoundingBox.{x,y}` - all three matched `golden-elements.json`'s existing values to 2 decimal
places, confirming `scale == 1.0` exactly for this frame (`canvas_match_value: 1` fully weights
height, and `source_frame.h` (2622) already equals `canvas_reference.h` (2622), so the scale factor
collapses to 1 - see `normalize.ts`'s `computeScaleFactor`). Applied the same transform to the 3 real
sub-nodes and corrected `golden-elements.json`:
- `button_x_frame`: `{x: 991.18, y: 188.87, w: 120.62, h: 128}` (was `{986.64, 188.87, 128, 128}`)
- `button_x_base`: `{x: 997.43, y: 194.90, w: 108.12, h: 115.60}` (same "was" as above)
- `button_x_icon`: `{x: 1013.46, y: 211.58, w: 76.52, h: 76.52}` (was briefly hand-estimated as
  `{1018.64, 208.07, 64, 64}` per the user's "roughly 50%, centered but a bit upper" description,
  before the real cache data was found - the real numbers land at ~60% size, confirming the user's
  visual estimate was directionally right, just not pixel-exact, as expected of an eyeballed guess)

Rebuilt and verified: all 3 elements' `RectTransform`s in the saved prefab now match these exact
values.

**New helper, built specifically so this class of bug can't recur silently**:
`packages/mcp-tool/src/figma/node-rect.ts` (`computeNodeRect`) + a new `ui-assembler figma-node-rect
<figma-node-id> [element-tree.json]` CLI subcommand (`cli.ts`) - given any Figma node id found inside
an already-cached frame export, prints its rect in `canvas_reference` space using the exact transform
above. Reuses `parse-tree.ts`'s frame-relative rect math and `normalize.ts`'s `computeScaleFactor`
directly (not a re-derived copy - the whole point is one path for this transform, not a second one
that can drift). Reads `FIGMA_FILE_KEY`/`FIGMA_ACCESS_TOKEN` from `.env` and `frame_id`/
`canvas_reference`/`canvas_match_mode`/`canvas_match_value` from the given element-tree.json (default
`fixtures/golden-elements.json`) rather than hardcoding them a third time. Verified against all 3
corrected `button_x` children AND `icon_heart` (a known-good, already-real element) - all four
outputs matched exactly. **Use this any time a golden-elements.json rect needs to correspond to a
real Figma node - never hand-type/eyeball one again**, per the user's own conclusion when asked
"should golden-elements build from Figma?": the WHICH/type/hierarchy judgment calls must stay
hand-labeled (that's what Gate 1 measures), but a rect for a real node is a mechanical fact and
should always be pulled through this tool.

**Open architectural question this raises, NOT acted on this session**: if real sub-layer geometry
for `button_x`'s composite is recoverable from the Figma export after all, `reduce.ts` (or a new
step) could in principle learn to decompose a component instance into its constituent sprites
automatically, rather than this being permanently fixture-only hand-authored ground truth (contra
what "Composite elements" above previously concluded). This needs real design (how would `reduce.ts`
know WHICH instances to decompose vs. keep flat, and against what signal - layer naming patterns?
matching sub-layer names against catalog asset names?) - flagging as a real, re-opened question for
whoever next touches `reduce.ts` or the composite-matching path, not resolved here. The
`figma_node_id`s for these 3 children were deliberately left as `synthetic:*` (not updated to the
real discovered node ids like `I178:34527;82:1405`) to avoid silently changing Gate 1 scoring
semantics (score_gate1.py excludes `synthetic:`-prefixed elements) - that's a separate decision from
today's rect-accuracy fix and shouldn't be made as a side effect of it.

## button_continue decomposed into 2 real composite children - REVERTED (session addition)

**Superseded later in the same session** - see "UI.Image Sliced-rendering bug: RESOLVED" below for
the full account of what actually shipped. Kept in full below because the underlying finding (the
real Figma mockup DOES structurally mirror `ButtonFrame.prefab`'s own
root/child split) is still true and useful context - only the "so decompose it into 2 separate
sprite elements" conclusion was wrong. The user's own correction: the project already has
`UIElements__ButtonFrame`, a prefab TEMPLATE with exactly this frame+main structure built in -
reconstructing it from 2 independent sprites duplicates what the template already does, and (as the
next section covers) surfaced a real rendering bug in the process. `button_continue` is back to a
single element matched to the `ButtonFrame` prefab, same as it was right after the first
`button_continue` fix earlier in this file.

The "open architectural question" immediately above got answered sooner than expected, and by the
user directly: they re-exported the Figma frame with 2 new named layers under `button_continue` -
`button_continue_frame` and `button_continue_main` - specifically so this tool could detect and use
the decomposition, the same pattern `button_x` already established. Read the updated
`.cache/figma/bvMnEkqW7u3CHT5lGNs3lg/178-35186.json` and confirmed both real sub-nodes exist with
real geometry (`button_continue_frame`: full 838x285 footprint; `button_continue_main`: inset to
826.72x273, a few px margin on each side). Used the new `figma-node-rect` CLI tool (built earlier
this session, see above) to pull both rects mechanically rather than re-doing the by-hand transform
a third time.

**Also explains the earlier `ButtonFrame`-prefab-nested-child bug from a different angle**: checked
which sprite `button_continue_frame`'s real Figma geometry should map to, and found it's
`UIElements__button_frame_blue` - confirmed by matching sprite GUIDs, this is the EXACT SAME sprite
asset `ButtonFrame.prefab`'s own root Image already uses internally as its decorative outer layer.
Likewise `button_continue_main` maps to `UIElements__button_green` - the same sprite
`ButtonFrame.prefab`'s inner `ButtonContinue` child already uses. So the real Figma mockup's own
frame/main split mirrors the prefab's own root/child split almost exactly (frame ~838x285 outer vs.
main ~827x273 inner - only a ~1-2% margin either way) - not a coincidence, the prefab's nested
structure IS the frame+main composite, just baked into one asset instead of two.

Restructured `golden-elements.json`'s `button_continue` the same way `button_x` is structured: kept
as a real, Gate-1-scored top-level element (`figma_node_id: "178:34525"` unchanged), with 2
`synthetic:`-prefixed leaf children carrying the real rects above. Replaced the old single
`button_continue` entries in `golden-matches.json` and `.cache/match-result.json` with
`button_continue_frame` → `UIElements__button_frame_blue` and `button_continue_main` →
`UIElements__button_green` (both plain sprites - the nested-prefab-resize case from the earlier bug
fix doesn't even apply to this element anymore, since it's no longer matched to the whole
`ButtonFrame` prefab). Rebuilt and verified: both children's `RectTransform`s in the saved prefab
match these exact values, sitting as direct siblings in frame-then-main order (correct back-to-front
draw order). Re-scored Gate 2: FAR 0%, auto-accept **87.5%** (K grew 7→8), missing-recall 100%,
still PASS - see `scoring/report.md`'s "Second update" section.

**Still open**: whether `button_upgrade` (matched to `UIElements__ButtonFrameTint`, the SAME
root/child nested structure) should get the same frame+main decomposition. Not done - `button_upgrade`
is a synthetic top-level element (fabricated for Gate 2 tint testing, doesn't exist in the real
Figma frame), so there's no real sub-layer geometry to pull for it the way there was for
`button_continue`/`button_x`. It stays matched as a single element to the whole prefab, relying on
`NodeBuilder.ResizeMainImageToFill` (the earlier bug fix) to resize its inner child correctly - which
it does, just without preserving the small frame/main padding the real composite pattern has.

## UI.Image Sliced-rendering bug: RESOLVED (session addition)

The user rejected the `button_continue` decomposition above and pointed at something more basic:
`button_continue_frame` (rendered via `NodeBuilder.BuildFromSprite`, `UIElements__button_frame_blue`,
Sliced) showed as a flat, completely unrounded rectangle in the Unity Editor - not a subtle
proportion issue, a totally flat rect with no border effect visible at all. They also asked directly
whether this had actually been visually validated before being reported as done - it hadn't (only the
saved prefab's serialized field values had been checked, never a rendered pixel) - a real gap, called
out fairly.

**Root cause, found by building a real screenshot-diagnostic tool and comparing rendering paths side
by side** (`SmokeTest.CaptureCatalogEntryRender` - builds via the exact real `NodeBuilder.BuildFromSprite`/
`BuildFromPrefab` methods, Screen Space - Camera + Constant Pixel Size Canvas, renders to a
RenderTexture, writes a PNG; `SmokeTest.CaptureRawTextureDrawTexture` - the control, same border data
via raw `Graphics.DrawTexture` against the loose texture): **`UnityEngine.UI.Image`'s own built-in
Sliced-border rendering is broken for this project's sprites** - confirmed real, not a border-math or
resize-math bug on this repo's side: the SAME sprite, SAME border array, SAME target size (even the
sprite's own NATIVE 256x256 size, no resize at all) rendered as a flat rectangle via `UI.Image` +
`Sliced` type, while `Graphics.DrawTexture` against the identical raw texture + identical border ints
rendered correctly (visible rounded corners, visible cyan outline detail) every time. Ruled out, with
real evidence rather than assumption, before concluding this:
- NOT a border-unit conversion bug on our side (`sprite.border`, `pixelsPerUnit`, `pixelsPerUnitMultiplier`,
  `canvas.referencePixelsPerUnit` all logged and matched expected values exactly).
- NOT a Canvas/Camera render-target ordering bug (fixed a real but separate ordering issue -
  assigning `cam.targetTexture` before creating the Canvas, since Screen Space - Camera sizes its own
  geometry from the camera's target - but this alone didn't fix the flat-rectangle symptom).
- NOT specific to a resized target (reproduced identically at the sprite's own native 256x256 size,
  no resize at all - so this isn't a resize/9-slice scaling-math bug on our side either).
- NOT fixed by `EditorSettings.spritePackerMode = Disabled`, ruling out a simple "atlas packing isn't
  active in batch mode" theory (though the atlas IS real and active: `sprite.texture` resolves to a
  4096x4096 `ASTC 6x6`-compressed `SpriteAtlasV2` atlas texture, and the asset's own import settings
  use `spriteMeshType: Tight`, matching `RenderedThumbnail.cs`'s own years-old comment that Tight mesh
  type breaks `SpriteRenderer.drawMode = Sliced` - this appears to be the same underlying class of
  issue, now hit for the first time via `UI.Image` rather than `SpriteRenderer`, since `NodeBuilder` is
  the first code in this project to actually put a real `UI.Image` on one of these sprites).

**Fix**: don't trust `UI.Image`'s built-in Sliced rendering for these sprites at all. `NodeBuilder`
now pre-composites Sliced sprites itself via `Graphics.DrawTexture` against the loose source texture
(literal texture-pixel border ints - the exact convention `RenderedThumbnail.cs`/`render-candidate.ts`
already established and this session's diagnostic just re-confirmed works), at the element's exact
target pixel size, then displays the composited result as a plain `Simple`-type sprite
(`Sprite.Create` off a runtime `Texture2D`). Applied in two places:
- `NodeBuilder.BuildFromSprite` now takes the element's target rect directly (not just resized after
  the fact by `PositionRect`) so it can pre-composite at the right size up front.
- `NodeBuilder.ResizeMainImageToFill` (the nested-prefab-child fix from earlier this session) does the
  same pre-composite for a prefab's main image when it's Sliced, reusing `entry.Border`/`entry.ImageType`
  directly since `RenderMetadataProbe.ResolveMainImage` already probed them off this exact child.
- `CatalogEntryData`/`AssemblerJson.LoadCatalog` gained a `Border` field (previously trimmed as
  unneeded, back when the plan was "let Unity's own Sliced rendering read border off the Sprite" -
  no longer true).

**Verified with real renders, not just re-reading code**: `UIElements__button_frame_blue` at 838x285
(the assembled `button_continue`'s actual target size) now shows correct rounded corners + the cyan
outline detail; the whole `UIElements__ButtonFrame` PREFAB (root decorative wrapper + inner
`ButtonContinue` child, both Sliced) renders correctly end to end; `UIElements__button_frame_x` (the
X button's frame layer, a completely different sprite) also renders correctly at its own target size
- confirming the fix generalizes, not a one-asset patch. Rebuilt the actual assembled prefab
end-to-end afterward and re-scored Gate 2: still **PASS** (FAR 0%, auto-accept 85.7%, missing-recall
100%, K=7 - back to the count from before the reverted decomposition, since `button_continue` is one
element again).

**`SmokeTest.CaptureCatalogEntryRender`/`CaptureRawTextureDrawTexture`** are left in the codebase as
reusable diagnostics (same convention as `DumpImageStates`/`DiagnoseThumbnailArtifact`/
`ProbeDrawTextureBorderSemantics`) - genuinely useful if a similar Image-vs-Sprite-vs-raw-texture
rendering mismatch shows up on a different asset later. `CaptureCatalogEntryRender` in particular
directly reuses `NodeBuilder`'s own real methods (`internal`, not `private`, specifically so this
diagnostic can call them) - it renders exactly what the real assembler builds, not a parallel
approximation of it.

**Open question, not chased further**: the precise Unity-internal reason `UI.Image`'s Sliced path
fails for a Tight-mesh-type sprite packed into a compressed `SpriteAtlasV2` atlas wasn't root-caused
down to Unity's own source - only empirically confirmed as broken and worked around. If this surfaces
again on a asset NodeBuilder doesn't already cover (e.g. a Tiled-type sprite, none exist in this
catalog currently), the same `RenderSlicedToTexture` pre-composite approach is the known-working
pattern to reach for first, rather than re-investigating from scratch.

## Non-obvious operational facts

- **Figma REST API is capped at 6 requests/month** (free tier) - do not call it repeatedly during
  dev. Use `packages/mcp-tool/figma-plugin/` (runs inside Figma, not metered) to (re-)populate
  `.cache/figma/<fileKey>/<nodeId>.json`; the pipeline reads that cache first always.
- **`reduce.ts` AND `visual-signal.ts` both go through `src/agent/`** (the agent-CLI adapter,
  see above), which shells out to the `claude` CLI rather than `@anthropic-ai/sdk` (removed as a
  dependency) - avoids needing a separate `ANTHROPIC_API_KEY` by reusing this environment's login.
  Needs the CLI's interactive login, so this can't run unattended in CI yet - and a nested `claude
  -p` needs `--add-dir` to read files outside its own working directory (see above), which the
  adapter already handles for any image paths passed to `complete()`.
- **Unity batch mode must omit `-nographics`** for anything that renders - it silently returns
  flat/uninitialized `RenderTexture` output on this machine otherwise.
- **`GameObject.activeInHierarchy` is unreliable on prefab assets** loaded via `AssetDatabase`
  outside a scene - always reports `false` even when genuinely active. Use
  `RenderMetadataProbe.IsActiveUpToRoot` (manual `activeSelf` walk) instead.
- `reduce.ts` has real run-to-run variance on borderline elements (observed: `icon_glow`,
  sometimes included, sometimes not). This is expected LLM behavior, not a bug - don't try to
  eliminate it by overfitting the prompt to one fixture.
- Composite button prefabs can have several `Image` components (decorative frame, disabled-state
  siblings, sprite-less placeholders). `RenderMetadataProbe.ResolveMainImage` picks the one a
  `Selectable`'s `targetGraphic` points to, filtered to only visually-contributing images
  (enabled, has a sprite, alpha > 0, active up the chain) - falls back to the largest by area if
  there's no usable `targetGraphic`. `SmokeTest.DumpImageStates` is a reusable diagnostic for
  inspecting any prefab's Image states when this needs debugging further.

## What's not done yet

- T1.7 (LLM catalog descriptions) - skipped, not blocking.
- ~~Fix the thumbnail rendering bug~~ - **RESOLVED this session**, see "Thumbnail rendering bug:
  RESOLVED" above for the full story (real cause: unscaled-vs-scaled border units, not atlas
  packing). Worth a final rebuild + visual spot-check if this file is picked up in a fresh
  session and `.cache/catalog.json` might be stale relative to the current
  `RenderedThumbnail.cs`.
- ~~Synthetic golden elements' crops don't correspond to real content~~ - **RESOLVED this
  session**, see "Synthetic-element crop quality: RESOLVED" above for the fix (hand-authored
  `fixtures/synthetic/*.png` ground-truth images, `resolveElementCrop` routing) and real
  before/after results (FAR 33.3%->0%, missing-recall 66.7%->100%).
- ~~Composite children share one crop~~ - **RESOLVED this session**, see "Composite crop-sharing:
  RESOLVED" above for the fix (joint compositing via `composite-visual-signal.ts`) and real
  before/after results. One residual case (`button_x_frame`) remains wrong, but for a different,
  already-understood reason (coarse visual signal can't discriminate two near-identical red
  frames) - not the crop-sharing bug this section was about.
- T2.7 (threshold tuning) - **substantially done, and now lower-priority than it looked**: thresholds
  are tuned against a real topK=10 run on the fixed catalog with the pure-JS visual signal (see
  above) - `MATCH_VISUAL_THRESHOLD=0.4`, `MISSING_VISUAL_THRESHOLD=0.24`, `MARGIN_THRESHOLD=0.12` in
  `gate.ts`. Gate 2 reached a real PASS (see "Week 3: the assembler" below) via golden-fixture
  correction + human review, not via matcher/threshold changes - the matcher's own raw output for
  `icon_glow`/`button_x_frame`/`button_continue` is unchanged and would still need this tuning if the
  goal shifts to raising the AUTOMATED auto-accept rate itself (currently still the same 42.9% the
  matcher alone produces) rather than relying on human review for the residual cases.
- ~~T2.8 (Gate 2 readout / go-no-go write-up)~~ - **DONE this session**, see `scoring/report.md`
  (Gate 1 + Gate 2 readout, go/no-go recommendation: proceed to Week 3 behind mandatory review on
  `uncertain` results). Rerun `score_gate1.py .cache/element-tree.json` and
  `score_gate2.py .cache/match-result.json` and refresh the numbers in that file if either cache
  artifact changes before this is picked up again.
- `fetch-frame`/`reduce` are now wired into `cli.ts` as real subcommands: `ui-assembler
  fetch-frame [--refresh]` (reads `FIGMA_FILE_KEY`/`FIGMA_NODE_ID` from `.env`, cache-first same
  as `scripts/fetch-figma-frame.mjs`) and `ui-assembler reduce [output.json]` (fetch-frame →
  parseTree → reduce via the shared agent adapter → normalize → write element-tree.json, default
  `.cache/element-tree.json`). CanvasScaler config is hardcoded in `cli.ts` to match the fixture
  (`referenceResolution={1206,2622}`, `matchMode=match_width_or_height`, `matchValue=1`) - same
  pin `fixtures/golden-elements.json` uses; `source_frame` still comes from the fetched frame's
  own `absoluteBoundingBox` at runtime. `fetch-frame` verified end-to-end against the cache
  (`fetched "Lose Screen" (178:35186) -> .cache/figma/.../178-35186.json`); `reduce` typechecks
  but was not run end-to-end this session to avoid consuming agent-CLI quota - the plumbing is
  a straight compose of the same three functions `HANDOFF.md`'s Week 1 notes already describe as
  individually working. `build-catalog-descriptions` (T1.7) is still unwired, still non-blocking.
  **Verified end-to-end** post-write-up: `ui-assembler reduce` produced 7 elements, wrote
  `.cache/element-tree.json`, and `score_gate1.py` on that output reproduced the golden Gate 1
  numbers (recall/precision/hierarchy 100/100/100, PASS). **Real quota cost, worth budgeting for
  next time**: this one `reduce` run consumed ~35% of the user's 5-hour Claude subscription
  window - the adapter avoids a metered BILL but not the subscription's rolling usage LIMIT.
  Don't casually re-run `reduce` (or anything else routed through `src/agent/`) as a
  verification step; diff against the cached `.cache/element-tree.json` instead.
- ~~Week 3 (assembler: `CanvasScaffold.cs`, `NodeBuilder.cs`, `PrefabWriter.cs`)~~ -
  **implemented and run end-to-end**, see "Week 3: the assembler" above.
- ~~T3.6 (`scoring/report.md`'s Week 3 section - assembled-prefab screenshot
  next to the Figma frame, go/no-go write-up)~~ - **DONE**: user captured
  `fixtures/assembled-prefab.png` from the Unity Editor and it's embedded
  side-by-side with `fixtures/frame-export.png` in `scoring/report.md`'s new
  "Week 3 visual result" section, with an explicit Week 3 Go/No-Go: PASS. The
  live open-and-compare in the Editor was done by the user across multiple
  review passes (see "Update after Week 3's real prefab review" and "Second
  review pass" in this file); a batch-mode pixel-diff renderer against
  `frame-export.png` is still not built, and flagged not-blocking.
