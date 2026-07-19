# Handoff

Session summary for picking this back up fresh. See `README.md` for setup/commands; this file is
about *state* and *things that aren't obvious from reading the code*.

## Where things stand

The vertical slice itself is **complete end-to-end** (this is the `slice` branch, untouched).
Gate 1: PASS (100/100/100 recall/precision/hierarchy). Gate 2: currently SOFT PASS on the
matcher's raw output - FAR 0%, auto-accept 42.9%, missing-recall 100% (see "Gate 2 tuning" below
for what 42.9% is and isn't fixable by tuning). A prior run reached a full PASS (85.7%
auto-accept) via a golden-fixture correction plus a human review pass promoting 3 `uncertain`
entries after manual confirmation - that's not a matcher improvement, it's the honest path to
full PASS today (see `scoring/report.md` for the complete history of readouts). Week 3 (the
assembler) is implemented, produces a real `.prefab` from the "Lose Screen" fixture, and has been
reviewed live in the Unity Editor by the user across several passes against the real Figma
mockup.

**Work has since moved to `graduation`** (this branch, cut from `slice` at `c6fdf53`): turning the
slice into an actually-usable tool rather than just a pipeline that passes accuracy gates. All
code is implemented, and the full pipeline - including fallback-asset generation for Combine
groups (a same-day pivot away from the original per-child composite decomposition) - has now been
verified end to end for real against `button_x`: Combine in the Figma plugin -> capture ->
`fallback_eligible` review row -> Import as New Asset -> Save & Assemble -> a correct-looking
prefab, confirmed by the user. **See "Graduating the slice" below for the four real bugs that
surfaced along the way (all fixed) and the one still-open limitation.**

Companion artifacts (fetch via WebFetch if needed - not saved locally):
- Full system design: `https://claude.ai/code/artifact/c61dba5d-c7c7-437b-85c7-fc27b88e1009`
- Vertical-slice spec (the two gates, exact thresholds): `https://claude.ai/code/artifact/62438512-43c8-4274-a143-1cf55359c916`
- Implementation plan (day-by-day tasks): `https://claude.ai/code/artifact/266f43a5-db5b-4d8f-b449-96b1266c248b`

## The fixture

- Target project: `/Users/dungphan/Melon` (a real, separate git repo - Disney.RainbowCore-based
  game). Ask the user directly for facts about it; don't explore it with subagents (see memory).
- Figma frame: "Lose Screen" (`178:35186`) in file `bvMnEkqW7u3CHT5lGNs3lg`.
- Feature folder: `Assets/Textures/UI/UI Elements` (62 sprites) + 3 explicit extra prefabs
  (`ButtonFrame`, `ButtonFrameTint`, `Scrim`). 65 catalog entries total.
- `fixtures/golden-elements.json`: 10 elements (7 real + 3 synthetic: `icon_gem`, `button_share`,
  `button_upgrade`, marked with a `synthetic:` `figma_node_id` prefix, excluded from Gate 1 scoring,
  used only for Gate 2 missing-recall/tint test cases).
- `fixtures/golden-matches.json`: matched/missing ground truth for Gate 2 scoring.

## Graduating the slice into a usable tool

**Branch: `graduation`, cut from `slice` at `c6fdf53` ("finish w3").** User's goal after the
vertical slice: a tool that's easy to use, fast, no-cost, and automatic - not just a pipeline that
passes accuracy gates. The real pain point driving this: the user doesn't want to round-trip back
to Figma to rename layers when `reduce.ts`'s LLM misses/misclassifies something - they want a
selection UI that shows ALL raw nodes with preview thumbnails so a human just clicks which ones to
assemble, no naming discipline required, no LLM call needed for filtering.

**UI surface: Stage 1 (selection) lives in a Figma plugin, not Unity; Stage 3 (match review) is a
Unity `EditorWindow`.** Original plan put both stages in Unity; superseded once the user proposed
doing selection inside Figma itself - Stage 1 needs the real mockup as a preview and native
multi-select, both free inside Figma, vs. faking thumbnails via UV-crop math off a flat
frame-export PNG. Stage 3 genuinely belongs in Unity - it needs catalog/rendered-thumbnail data
that doesn't exist until the matcher has run. Neither stage runs through an agent CLI - both are
pure UI/judgment-already-made tasks, and routing them through `getAgentAdapter()` would burn
subscription quota (see "Agent adapter" above) for what a checkbox tree or side-by-side image view
does for free.

**Six pieces, all implemented and compile-verified as of this branch:**

1. **Schema + `match.ts` composite flag.** `element-tree.schema.json` gained `composite: boolean`
   (explicit, author-set - real composites don't reliably share an identical rect, e.g. a glow
   layer sized differently from its frame, so geometry can't be trusted as the signal; a looser
   rect-overlap/IoU heuristic and a Figma-component-instance-boundary heuristic were both
   considered and rejected). `match.ts`'s `isCompositeGroup` reads the flag directly instead of the
   old rect-equality heuristic.
2. **`element-tree.schema.json`'s `container: boolean`**, independent of `composite` (a group can
   be composite, container, both, or neither) - true when children should be built as real nested
   GameObjects under a real parent transform rather than flattened to siblings under the canvas
   root. Originally added for `button_x` (animating frame/backing/icon independently), but
   `composite` groups are now always matched/built as ONE flat unit (see item below and "Fallback-
   asset generation") - `container` no longer applies to a clean Combine group, only to the case
   where an anchor has real extra content beyond its combine group (item 4 below).
   `NodeBuilder.BuildRecursive` threads
   `originX`/`originY` through the recursion so a child's always-absolute `rect` converts
   correctly to parent-relative coordinates once nested; container elements get a `center`-pivot
   `PositionContainerRect` (animation-friendly, confirmed with the user), scoped to containers only
   - every leaf element keeps the existing top-left `PositionRect` convention untouched.
3. **Figma plugin Stage 1 UI** (`packages/mcp-tool/figma-plugin/`): `code.js` (scene-graph access,
   `node.exportAsync`/`node.setPluginData`) + `ui.html` (DOM, talks to `code.js` via
   `postMessage`) now render a recursive checkbox tree (tri-state parents) with lazy per-node
   thumbnails via `exportAsync`, a type-tag picker, and an explicit "Combine" mode (toggle-then-
   pick-then-confirm, not ctrl/cmd-click - user's explicit choice) that marks a group's members via
   `node.setPluginData("uas:compositeGroupId", ...)` - durable, saved in the Figma file itself,
   survives re-exports. Deliberately separate from Figma's native Group/Frame grouping
   (Cmd/Ctrl+G), since designers already use native grouping for plain layout organization
   unrelated to "these become one Unity image." The plugin exports the full, unpruned tree
   annotated with `selected`/`typeTag`/`compositeGroupId`/`thumbnail` per node and nothing more -
   it does NOT itself restructure nodes into a wrapper or set the schema's `composite`/`container`
   fields; that's the Node-side reducer's job (next item). User-verified live against the real
   901-node "Lose Screen" frame: tree loads, thumbnails render with no lag, checkbox/type-tag
   capture correctly, Combine correctly groups/excludes the right nodes and the badge persists
   across a full plugin re-run. One real bug found and fixed: `figma.fileKey` is undefined by
   default - needs `"enablePrivatePluginApi": true` in `manifest.json` (present now).
4. **Node-side reducer** (`packages/mcp-tool/src/figma/reduce-from-selection.ts`, CLI:
   `ui-assembler reduce-from-selection <plugin-export.json> [output.json]`): a deterministic,
   non-LLM alternative to `reduce.ts` that turns a plugin export into a real `element-tree.json`
   via the same `normalize()` call. Two real design traps found by verifying against an actual
   plugin export (not synthetic data), both fixed: (a) a combine group's "anchor" member can be
   the real Figma PARENT of its other members, not a sibling - fixed via a global (not
   sibling-scoped) tree walk partitioning each group into an `anchor` (contributes identity/rect
   only) vs. `leaves` (the members actually matched/composited); (b) an anchor can ALSO have real
   selected content beyond its combine group (e.g. deliberately-not-combined sibling text) - the
   anchor becomes a real `container: true` element holding a synthesized composite sub-element
   (`id` suffixed `_combined`, rect = union of just the leaf members) alongside its own
   normally-processed extra children. Also handles duplicate layer names across the tree
   (deterministic `id` disambiguation via the Figma node id, not an arbitrary counter). Verified
   end to end against a real plugin export -> real catalog match run, zero errors.
5. **`candidates.ts` weak-signal retrieval fallback.** Removing naming discipline doesn't just
   weaken `reduce.ts`'s classification - the retrieval pre-filter that narrows the ~65-entry
   catalog to topK before real scoring depends entirely on `element.id`/`type`/
   `visual_description` token overlap, so an unnamed node can get the correct asset excluded
   before visual comparison ever runs. Original idea (type/category tag matched against catalog
   `role`/`feature`) was checked against the real catalog and doesn't work - `role`/`feature` are
   uniform across all 65 entries, no real signal there. Shipped fix: `isWeakSignal` detects
   elements with a generic Figma default name AND generic/absent type, and for those specifically
   skips the narrowing pre-filter, sending the (near-)full catalog to real scoring instead.
   Degradation is graceful, not silent: `structuralSignal` stays noisier for these,
   `gate.ts`'s agree-by-rank check fires less often, more elements land as `uncertain` for Stage 3
   review - confirmed on real data (`matched` dropped 6->2, `uncertain` rose 8->12), the intended
   tradeoff, not a regression. **Open risk, not yet re-tested**: whether pure visual signal has
   enough discriminating power to rank within a growing candidate set without the token
   pre-filter's precision - HANDOFF already documents known near-duplicate confusion (`icon_glow`
   vs `icon_heart`, `button_x_frame`) as the risk case this could make worse.
6. **Stage 3 Unity `EditorWindow`** (`packages/unity-editor/Editor/Review/`): `ReviewWindow.cs`
   (`UI Assembler > Review Window` menu item) + `PipelineRunner.cs` (non-blocking
   `System.Diagnostics.Process` chain runner - polls `HasExited` via `EditorApplication.update`
   rather than the off-thread `Process.Exited` event, since `OutputDataReceived`/
   `ErrorDataReceived` fire on a ThreadPool thread and can only safely enqueue strings into a
   `lock`-guarded queue drained on the main-thread tick). Runs `reduce-from-selection` -> `match`
   as chained subprocesses with a live streaming log, then shows one review row per
   `match-result.json` entry (left preview: Stage 1's own plugin thumbnail via a new
   `element-thumbnails.json` sidecar keyed by `figma_node_id`; right preview: the catalog's
   rendered thumbnail, now a file on disk per `ThumbnailPath` - see "Incremental catalog build"
   below) with Accept/Reject/Reassign (`GenericMenu`, text-only catalog picker). "Save & Assemble"
   writes edits back to `match-result.json` then calls the same three calls `RunAssemble.cs`'s
   batch entry point makes (`CanvasScaffold`/`NodeBuilder`/`PrefabWriter`), in-process instead of
   headless. Compile-verified with real Unity batchmode runs (imports/compiles clean, `RunAssemble`
   itself still works end-to-end afterward) - **but the interactive GUI itself has never been
   driven**: buttons, the live log, texture decode/render, Accept/Reject/Reassign, the picker,
   Save & Assemble's real behavior. Unity batchmode can't drive an `EditorWindow`'s GUI headlessly;
   this needs the user to open the window in a real Editor session. Also unverified in this
   environment: whether a bare `"npm"` `ProcessStartInfo.FileName` resolves when Unity is
   GUI-launched given `nvm`-installed npm on the user's `PATH` - the window has an `npmPath`
   override field specifically for this, not a generic fix.

**Immediate next step is interactive, not more code**: open `UI Assembler > Review Window` in a
real Unity Editor session against a real Figma plugin export and work through it end to end - this
is the last unverified surface of the whole graduated pipeline. Likely follow-ups depending on
what that surfaces: the `npmPath` gotcha, GUI polish, or bugs in the review-row/texture-decode
logic.

**Fallback-asset generation for Combine groups with no catalog match - IN PROGRESS, plan at
`/Users/dungphan/.claude/plans/nifty-honking-platypus.md`.** Real motivating cases, both
confirmed live against the real "Lose Screen" pipeline: `button_continue_main` (plain shapes + a
text node, genuinely custom art, no reason to expect a catalog match at all) and, more
surprisingly, `button_x` (frame/base/icon - 3 layers that DO each have a real, correct,
already-existing catalog sprite, but scored wrong/too-low when matched per-child against
overlapping crops). The user explicitly decided, after weighing both cases, that a Combine group
should ALWAYS be treated as one node, matched or captured as one thing - never decomposed back
into its members, even when good per-member matches exist. Trigger/persistence decisions
(human-confirmed in review; imported as a real reusable `catalog.json` entry, not one-off) were
already confirmed before this and are unchanged.

**Phase A DONE** (schema + matcher, no live Figma/Unity needed - see the plan file for exact
diffs): `match-result.schema.json` gained a `"fallback_eligible"` status; `match.ts` no longer
decomposes composite groups into per-child `compositeVisualSignals` scoring (that file is
deleted) - a composite element is matched as ONE unit via the normal single-element path, and a
`missing` verdict flips to `fallback_eligible` when `MatchElementTreeOptions.fallbackCaptureIds`
(sourced from a new `.cache/element-fallback-captures.json` sidecar, same convention as
`element-thumbnails.json`) contains its `figma_node_id`; `reduce-from-selection.ts` now returns
`{ elements, fallbackCaptures }` and plucks a `combinedHiResExport` field (not yet populated by
the plugin - Phase B) off Combine-group members into that map. **Also part of this**: the
`container: true` addition documented in item 2 above (for `button_x`'s "anchor, no extra
children" case) was reverted - a Combine group matched/built as one flat unit has nothing left to
animate independently.

**Phases B and C also DONE, compile-verified, NOT YET INTERACTIVELY TESTED** - see the plan file
for exact diffs. Phase B: `code.js`'s `handleConfirmCombine` now also resolves the group's real
Figma anchor (independently detected there, live against real nodes - same anchor concept
`reduce-from-selection.ts` detects on the exported JSON) and, if one exists, hides its TEXT
descendants, `exportAsync`s it at native resolution (no `WIDTH`/`SCALE` constraint, unlike the
64px thumbnail path), restores visibility, and posts a new `combinedExport` message; `ui.html`
attaches it as `combinedHiResExport` on every group member (same redundant-per-node approach the
`thumbnail` field already uses). No-anchor pure-sibling groups are explicitly out of scope for
this pass (logged, skipped - that group still gets `composite: true` normally, just no fallback
capture, i.e. today's plain `missing` behavior, not a regression).

Phase C: `ReviewWindow.cs` gained an "Import as New Asset" button for `fallback_eligible` rows
(shows the hi-res capture as the left preview instead of the small selection thumbnail) wired to a
new `ImportFallbackAsset` action - writes the capture as a real PNG under
`Assets/_Generated/UIAssembler/FallbackAssets/`, imports it via a new shared
`AssetImportHelpers.ImportPngAsSprite` (factored out of `NodeBuilder.SaveCompositedSprite`, which
now calls the same helper - see "Thumbnail/prefab rendering" below for why a real file, not an
in-memory `Sprite.Create()`, is required), probes it via the existing `RenderMetadataProbe`
(confirmed gracefully handles a plain no-border PNG), renders its thumbnail via the existing
`RenderedThumbnail.RenderToPngBytes`, and appends a new `catalog.json` entry via a new
`CatalogAppender` (no writer for this existed before - confirmed via exploration only
`RunCatalogBuild.cs` ever wrote `catalog.json`, via a private, non-reusable encoder; the new one
round-trips through `AssemblerJson`'s existing `JsonParser`/`JsonWriter`). Updates the in-memory
`_catalog`/row state too, so the new id is usable immediately without a reload.

**Interactively verified end to end for real, against `button_x`** (Combine parent+3-children in
the Figma plugin -> real capture with no baked text -> `fallback_eligible` row in Review -> Import
as New Asset -> Save & Assemble) - the user confirmed the resulting prefab looks correct. Getting
there surfaced four real bugs, all fixed, worth knowing about if this area gets touched again:

1. **`cli.ts`'s `runMatch` never actually loaded `element-fallback-captures.json`.** The
   `fallbackCaptureIds` plumbing existed on both ends (`reduce-from-selection.ts` writing the
   sidecar, `match.ts` checking the option) but nothing wired them together, so every composite
   group landed `missing` instead of `fallback_eligible`. Fixed in `cli.ts`.
2. **`NodeBuilder.cs` never knew about the `composite` flag at all** - `AssemblerJson`'s
   `ElementData`/`LoadElementTree` never parsed it from `element-tree.json`. Since composite groups
   now match as ONE unit under the wrapper's own id (not per-child), but `BuildRecursive` still
   only checked `Container` before deciding whether to recurse into children, every composite
   element fell into the "pure grouping container" branch and recursed into children with no
   match-result entries of their own - building NOTHING for the whole group (`button_x`,
   `button_continue_main` both silently produced empty prefabs). Fixed: added `Composite` to
   `ElementData`, parsed it, and `BuildRecursive` now checks it before the children branch.
3. **A full `RunCatalogBuild.Run()` silently drops/prunes anything outside its own scan scope** -
   confirmed for real, not just theoretical: running `scripts/build-catalog.sh` for an unrelated
   compile check dropped the just-imported `Fallback__button_x__178-34527` catalog entry AND pruned
   its thumbnail file, since neither is inside the scanned feature folder / `-extraPrefabPaths`
   allowlist. **Added a repair utility for this**: `RunCatalogBuild.RegenerateMissingThumbnails`
   (`-executeMethod ...RegenerateMissingThumbnails -outputPath <catalog.json>`) re-derives any
   missing thumbnail directly from its catalog entry's own already-recorded render metadata (no
   re-probe needed) - fills gaps only, doesn't touch existing files. The underlying scope gap
   itself is unchanged (see below) - this utility repairs the symptom when it happens, doesn't
   prevent it.
4. **`render-candidate.ts` throws an uncaught exception on a missing thumbnail file** (`ENOENT`
   propagates straight out of `matchElementTree`, killing the whole `match` step with no partial
   results) - this is what actually surfaced bug 3 above as a hard pipeline failure rather than a
   quiet gap. Not hardened here (would need a decision on fallback behavior - skip that one
   candidate? fail the whole element? - not made yet); worth revisiting if this recurs.

**Known, still-real limitation** (bug 3's root cause, not fixed, just now has a repair path): a
newly-imported fallback asset lives outside `RunCatalogBuild.cs`'s scanned feature folder /
`-extraPrefabPaths` allowlist, so a future FULL catalog rebuild will still prune/drop it unless
asset-discovery scope is widened - the same pre-existing "asset-discovery scope" limitation
already tracked below, not a new gap. `RegenerateMissingThumbnails` fixes the thumbnail half after
the fact; the catalog.json entry itself still needs re-importing (or hand-restoring) if a full
rebuild drops it.

- `button_x` (a close button) and `button_continue` are **composite elements** - one Figma layer
  a designer Combines in the plugin into a single visual unit (e.g. `button_x` = frame + backing +
  icon sprites layered together). Composite-ness is an explicit, author-set flag
  (`element-tree.schema.json`'s `composite: boolean`), not inferred from rect equality - see
  "Graduating the slice" below for why that changed and how it's marked. `match.ts`'s
  `isCompositeGroup` reads the flag directly; a `composite: true` element is matched as ONE unit
  against its own rect/crop (NOT decomposed into its children - an earlier per-child joint-scoring
  design was replaced, see "Fallback-asset generation" below for why); everything else with
  `children` stays a pure grouping container that only matches its children.

## Architecture decisions worth knowing before touching related code

**Agent adapter** (`packages/mcp-tool/src/agent/`): `reduce.ts` shells out to the `claude` CLI
(subscription/OAuth auth, not a metered `ANTHROPIC_API_KEY`) via an `AgentAdapter` interface -
deliberately agent-agnostic (`registry.ts` is the extension point for another coding-agent CLI, or
eventually a Unity AI Assistant adapter running Editor-side instead of Node-spawned - researched,
not implemented, no license to test against). `visual-signal.ts` USED to go through the same
adapter but was rewritten as pure local pixel math (SSIM + color histogram, `ssim.js`, zero LLM
calls) after a real topK=10 run exhausted the user's Claude subscription quota mid-run - a nested
`claude -p` call per (element, candidate) pair doesn't scale. **Real cost, worth budgeting**: one
`reduce.ts` run alone consumed ~35% of a 5-hour subscription window - don't casually re-run
`reduce`/anything through `src/agent/` as a verification step; diff against the cached
`.cache/element-tree.json` instead. A nested `claude -p` also doesn't get filesystem read access to
arbitrary paths for free - needs `--add-dir <parent-dirs>`, which the adapter already handles.

**Composite elements**: real sub-layer geometry for a composite (e.g. `button_x`'s frame/base/icon
rects) IS recoverable from the Figma plugin export cache
(`.cache/figma/<fileKey>/<nodeId>.json`) even when reduce.ts itself can't decompose a component
instance automatically - use `ui-assembler figma-node-rect <figma-node-id>` (`node-rect.ts`) to
pull a real node's rect mechanically rather than hand-typing/eyeballing one. `reduce.ts` (the LLM
path) still has no way to know WHICH instances need decomposing from the Figma tree alone; the
graduation branch's answer is to sidestep this with explicit human marking in the Figma plugin
instead of solving auto-detection (see "Graduating the slice" above). Composite children need
JOINT visual scoring (`composite-visual-signal.ts`) -
naively, `cropElementFromFrame` would give every child the identical full-composite crop (all
layers visible) since they share a rect; visual scoring instead composites candidate renders from
ALL of a group's children together (coordinate-ascent local search, not exhaustive) and compares
the whole thing against the shared crop.

**Synthetic golden elements** (`icon_gem`/`button_share`/`button_upgrade`, fabricated for Gate 2
test coverage) don't correspond to real mockup content, so cropping them out of
`fixtures/frame-export.png` gives meaningless ground truth. `element-crop.ts`'s
`resolveElementCrop` loads a hand-authored `fixtures/synthetic/<element.id>.png` instead for any
element whose `figma_node_id` carries the `synthetic:` prefix. If you ever hand-author a reference
image for this: give it full opaque coverage, no transparent margins - `toComparableImage`'s
gray-flatten treatment turns transparent regions into a uniform mid-gray that can spuriously
inflate similarity scores against unrelated real candidates.

## Thumbnail/prefab rendering - hard-won rules, don't relitigate these

- **Never trust `UnityEngine.UI.Image`'s built-in Sliced-border rendering** for this project's
  sprites (Tight mesh type + packed/compressed `SpriteAtlasV2` atlas) - confirmed broken via
  side-by-side comparison against `Graphics.DrawTexture` on the identical raw texture/border/target
  size (even at native size, no resize). Both `RenderedThumbnail.cs` (catalog thumbnails) and
  `NodeBuilder.cs` (the real assembler) pre-composite Sliced sprites themselves via
  `Graphics.DrawTexture` against the loose source texture, then display the result as a plain
  `Simple` sprite. Diagnostics left in `SmokeTest.cs` if this needs re-confirming on a new asset:
  `CaptureCatalogEntryRender`/`CaptureRawTextureDrawTexture`.
- **`Graphics.DrawTexture`'s border ints are literal, UNSCALED source-texture pixel counts** (not
  destination-space pixels - confirmed via `SmokeTest.ProbeDrawTextureBorderSemantics`), valid only
  relative to the sprite's own *native* pixel size. Never composite a Sliced sprite at a size
  smaller than native - the border sum can exceed the destination and there's no way to tell
  `DrawTexture` "sample N source px, draw M dest px" for border regions; it crops the corner
  artwork mid-curve instead of scaling it. For downscale targets, composite once at native size
  first, then a plain bilinear resample (`Graphics.Blit`, no border logic) down to the target -
  `RenderedThumbnail.cs`'s `CompositeAtSize`/`ResampleBilinear` is the reusable pattern.
- **A prefab's thumbnail should show the whole active hierarchy** (every visible Image + Text/TMP,
  correct z-order/position via a real Canvas+Camera render), not just
  `RenderMetadataProbe.ResolveMainImage`'s single pick - that method answers "which one Image
  represents this entry for matching," a different question from "what does this asset look like."
- **Stretch-anchored roots** (`anchorMin != anchorMax`, e.g. `Scrim`, designed to fill whatever
  screen it's placed in) have no size of their own outside a real parent Canvas - detect stretch
  from the anchors directly, don't infer it from a resolved `rect` read (a stretch-anchored rect
  against an arbitrary synthetic parent can read as a plausible non-zero number that's still
  wrong). Fall back to `RenderMetadataProbe`'s already-resolved `NativeWidth`/`NativeHeight`.
- **Layout Groups need an explicit forced rebuild** - `LayoutRebuilder.ForceRebuildLayoutImmediate`
  after instantiating a prefab, before rendering. A Layout Group only repositions children on an
  actual layout pass, which runs lazily (next UI update), not synchronously on
  `PrefabUtility.InstantiatePrefab` - absent a forced pass, whatever `anchoredPosition` happens to
  be serialized in the prefab file is what renders, which may or may not match a real rebuilt
  layout depending purely on when the prefab was last saved in the Editor.
- **A non-flat prefab catalog entry** (decorative wrapper root + a differently-anchored inner
  content child, e.g. `ButtonFrame`) needs its actual visible child resized, not just the root -
  `NodeBuilder.ResizeMainImageToFill` reuses `RenderMetadataProbe.ResolveMainImage` to find and
  stretch-anchor the real content child to fill its parent.
- **`GameObject.activeInHierarchy` is unreliable on prefab assets** loaded via `AssetDatabase`
  outside a scene - always reports `false` even when genuinely active. Use
  `RenderMetadataProbe.IsActiveUpToRoot` (manual `activeSelf` walk) instead.
- Composite button prefabs can have several `Image` components (decorative frame, disabled-state
  siblings, sprite-less placeholders) - `RenderMetadataProbe.ResolveMainImage` picks the one a
  `Selectable`'s `targetGraphic` points to, filtered to visually-contributing images (enabled, has
  a sprite, alpha > 0, active up the chain), falling back to largest-by-area.
  `SmokeTest.DumpImageStates`/`DumpLayoutComponents`/`DumpTextStates` are reusable diagnostics for
  inspecting a prefab's component states.

## Incremental catalog build

`RunCatalogBuild.cs` is incremental by default (not a full re-probe/re-render every run - needed
at real-project scale, thousands of assets, not just this fixture's 65). `AssetDatabase.
GetAssetDependencyHash(path)` (changes whenever the asset OR anything it depends on changes) is
cached in a sidecar `.cache/catalog-build-cache.json`, keyed by asset **path** (not `id`, so a
feature-folder rename doesn't force a re-render), deliberately NOT part of
`catalog-entry.schema.json` - build-internal bookkeeping the Node side never sees. `FORCE_FULL=true`
bypasses the cache (needed after changing `RunCatalogBuild.cs`/`RenderMetadataProbe.cs`/
`RenderedThumbnail.cs` themselves - a code change the content hash can't see). Pruning of stale
cache/catalog entries is free by construction (the new cache is only ever built from the current
run's discovered assets).

Thumbnails are separate PNG files (`.cache/thumbnails/<id>.png`), not inline base64 in
`catalog.json` - `thumbnail_path` in the schema is a path relative to catalog.json's own directory,
resolved to absolute exactly once at each side's load chokepoint (`loadCatalog` on the Node side,
`AssemblerJson.LoadCatalog` on the Unity side) so nothing downstream needs to know where
catalog.json physically lives. Written only when an entry is actually (re-)rendered; a cache hit
also requires the referenced file to still exist on disk before trusting it (falls through to a
real rebuild otherwise - guards against someone manually clearing `.cache/thumbnails/`). Orphaned
thumbnail files (asset deleted/moved out of scope) get pruned explicitly, since files on disk don't
disappear "for free" the way JSON map entries do.

Still not done: asset-discovery scope is a single hardcoded feature folder + an explicit prefab
allowlist (`-extraPrefabPaths`) - fine for this fixture, not for a real project's likely-scattered
UI asset layout.

## Gate 2 tuning

`gate.ts` combines visual (pixel/SSIM) + structural (name/token-overlap) signals with per-element
min-max normalization, weighted `WEIGHT_VISUAL=0.4`/`WEIGHT_STRUCTURAL=0.6` (structural-favoring -
see below), gated by `MATCH_VISUAL_THRESHOLD=0.35`, `MISSING_VISUAL_THRESHOLD=0.24`,
`MARGIN_THRESHOLD=0.05`.

**Why structural is weighted higher than visual**: several near-miss cases are near-visual-
DUPLICATES (e.g. three different frame sprites all scoring visual ~0.345-0.347 against the same
element) - min-max normalization stretches that noise-level spread across nearly the full 0-1
range, letting it drown out a genuinely more-informative structural signal under a visual-heavy
weighting. Found by collecting real per-candidate scores once and sweeping weight/threshold
combinations cheaply against cached data (much faster than re-running the real, expensive
image-rendering matcher per combination).

**42.9% auto-accept is the actual ceiling for weight/threshold tuning alone** on this fixture (a
fine-grained sweep, ~2000 combinations, confirmed no config scores higher while keeping FAR=0%/
missing-recall=100%). Two elements remain stuck on wrong candidate SELECTION regardless of
weighting - `icon_glow` (picks `icon_heart` over the correct `ui_img_glow`) and `button_x_frame`
(picks `ButtonFrame` over the correct `button_frame_x`) - both are cases where the coarse pixel-
based visual signal can't discriminate two visually-near-identical competitors. No gate threshold
can fix this: the underlying per-candidate scores already rank the wrong asset first. Getting past
50% auto-accept needs better candidate-ranking quality (a finer visual signal, or leaning harder on
structural for near-duplicate visual sets specifically), not more tuning.

## Non-obvious operational facts

- **Figma REST API is capped at 6 requests/month** (free tier) - do not call it repeatedly during
  dev. Use `packages/mcp-tool/figma-plugin/` (runs inside Figma, not metered) to (re-)populate
  `.cache/figma/<fileKey>/<nodeId>.json`; the pipeline reads that cache first always.
- **Unity batch mode must omit `-nographics`** for anything that renders - it silently returns
  flat/uninitialized `RenderTexture` output on this machine otherwise.
- `reduce.ts` has real run-to-run variance on borderline elements (observed: `icon_glow`,
  sometimes included, sometimes not) - expected LLM behavior, not a bug; don't overfit the prompt
  to one fixture run.
- Only one Unity instance can hold a project open at a time - batch mode fails fast (exit 134) if
  the Editor is already open on it; check `ps aux` first if a batch run might collide with a live
  Editor session.

## What's not done yet

- **Asset-discovery scope still doesn't cover fallback-imported assets** - a future full
  `RunCatalogBuild` run will still prune/drop a `Fallback__*` entry unless scope is widened (see
  "Graduating the slice" above); `RegenerateMissingThumbnails` repairs the thumbnail half after the
  fact but not the catalog.json entry itself.
- **`render-candidate.ts` isn't hardened against a missing thumbnail file** - throws uncaught,
  killing the whole `match` run rather than degrading gracefully (see "Graduating the slice"
  above). No fallback-behavior decision made yet (skip the one candidate? fail the element only?).
- T1.7 (LLM catalog descriptions) - skipped, not blocking.
- Gate 2 auto-accept rate above 42.9% needs better candidate-ranking quality (see "Gate 2 tuning"
  above), not more threshold work.
- `reduce.ts` (the LLM path) still can't auto-detect composite decomposition on its own - the
  graduation branch's Figma plugin sidesteps this with explicit manual marking (see "Graduating
  the slice" above) rather than solving auto-detection; `reduce.ts` itself is unchanged.
- `button_upgrade` (matched to `ButtonFrameTint`, same nested frame/child structure as
  `button_continue`) hasn't gotten the same frame+main decomposition `button_continue`/`button_x`
  have - it's a synthetic top-level element with no real Figma sub-layer geometry to pull, so it
  stays matched as a single element, resized via `NodeBuilder.ResizeMainImageToFill` (correct, just
  without the small frame/main padding the real composite pattern has).
- A batch-mode pixel-diff/SSIM score between the automated screenshot renderer's output
  (`scripts/screenshot.sh`) and `frame-export.png` isn't built - would need to handle known,
  out-of-scope gaps first (no TMP font/color styling data in `element-tree.json`) or the diff would
  be dominated by things that aren't rendering bugs.
- Asset-discovery scope (single hardcoded feature folder + explicit prefab allowlist) - see
  "Incremental catalog build" above.
