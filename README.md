# unity-ui-assembly-tool

Unity UI Assembly Tool: Mockup to Prefab

Currently building the three-week vertical slice: prove the pipeline (Figma frame -> reduced
element tree -> matched against a Unity asset catalog -> assembled prefab) on one real screen,
against two accuracy gates, before committing to the full system.

## Layout

```
packages/
  contracts/     shared JSON Schemas + generated TS types (source of truth for the
                 MCP-tool <-> Unity-Editor handoff)
  mcp-tool/      Node/TS - Figma adapter, LLM reduction, matcher
  unity-editor/  C# - UPM local package, asset catalog probe, minimal assembler
fixtures/        hand-labeled golden sets (Gate 1 / Gate 2 yardsticks)
scoring/         scripts that diff pipeline output against fixtures/
```

The MCP tool (Node) and the Unity Editor batch scripts (C#) are separate processes that never call
each other directly for this slice - they hand off through JSON files validated against
`packages/contracts/schemas/`.

## Setup

```
npm install
npm run build   # generates contracts/src/types.ts from the schemas, validates the
                 # schemas against illustrative examples, typechecks contracts + mcp-tool
```

`packages/unity-editor` is a local UPM package; import it into the target Unity project via
`Packages/manifest.json` (`"com.ui-assembler-slice.editor": "file:../../unity-ui-assembly-tool/packages/unity-editor"`).

### Figma access

The free-tier Figma REST API quota is **6 requests/month** - not viable for iterative dev. The
primary path is `packages/mcp-tool/figma-plugin/` (see its README): a local dev plugin that runs
inside Figma itself (not network-metered) and exports the selected frame straight to
`.cache/figma/<fileKey>/<nodeId>.json`. `fetch-frame.ts` and `scripts/fetch-figma-frame.mjs` both
read that cache path first, regardless of which path filled it, and only hit the REST API (using
`FIGMA_ACCESS_TOKEN` from `.env`, copied from `.env.example`) when explicitly forced - expect to
burn the monthly quota fast if you do.

### Agent access (Claude, no separate API key)

`reduce.ts` (T1.9) and `visual-signal.ts` (T2.3) both go through `packages/mcp-tool/src/agent/` -
an agent-CLI adapter, not a direct SDK call - which shells out to the `claude` CLI and reuses its
existing subscription/OAuth login rather than requiring a separate metered `ANTHROPIC_API_KEY`.
Requires the `claude` CLI installed and authenticated (`claude --version` should succeed).

This is a dev-time convenience, not the final design - the CLI needs an interactive login, so it
can't run fully unattended (e.g. in CI) yet. The adapter interface (`src/agent/adapter.ts`) is
deliberately agent-agnostic - Claude is the only implemented adapter today, but adding another
coding-agent CLI (or, longer-term, a Unity AI Assistant adapter that runs Editor-side instead of as
a Node-spawned CLI - see HANDOFF.md) means implementing that one interface and registering it in
`src/agent/registry.ts`, without touching `reduce.ts`/`visual-signal.ts`.

### Running Unity batch mode

`-batchmode` **must not** be combined with `-nographics` for anything that renders (catalog
thumbnails, T1.5+) - confirmed empirically that `-nographics` makes `Graphics.Blit`/`RenderTexture`
return flat/uninitialized output on this machine (see `scripts/logs/t1.5-nographics-probe.log` vs
`t1.5-graphics-probe.log`). Pure logic steps (asset discovery, smoke tests) work fine either way;
default to omitting `-nographics` for consistency:

```
"/Applications/Unity/Hub/Editor/<version>/Unity.app/Contents/MacOS/Unity" \
  -batchmode -projectPath <path-to-target-project> \
  -executeMethod <Namespace.Class.Method> -quit -logFile <path>
```

Only one Unity instance can hold a project open at a time - batch mode fails fast (exit 134) if the
Editor is already open on that project.

## Status

**Week 1 complete - Gate 1: PASS.** 3 official runs of the full parse -> reduce -> normalize
pipeline against the real "Lose Screen" fixture, scored against `fixtures/golden-elements.json`
(7 elements) via `scoring/score_gate1.py`: 2/3 PASS (100% recall/precision/hierarchy), 1/3 SOFT
PASS (85.7% recall - missed only `icon_glow`, a genuinely ambiguous glow-effect layer).
Precision and hierarchy were 100% in all 3 runs - zero false-positive noise, zero mis-parenting.
Clear to proceed to Week 2 (matcher, Gate 2) per the spec's fail-path gating.

Done: T1.1-T1.6, T1.8-T1.12. T1.7 (LLM catalog descriptions) skipped for now - not on Gate 1's
critical path.

**Week 2, started.** T2.1 done: `fixtures/golden-matches.json` (8 entries - 5 matched, 3 missing).
All 4 Gate 2 fixture requirements covered: 9-sliced, tinted (`UIElements__ButtonFrameTint`,
`#7349FF` - a real hand-made tinted prefab variant), differently-sized-template, and
missing-recall (3 cases). `RenderMetadataProbe.cs`/`RenderedThumbnail.cs` extended to support
prefab catalog assets, not just bare sprites - see task history for the real bugs caught along
the way: wrong Image resolution on composite prefabs, degenerate stretch-anchor sizing, unscaled
border corruption at non-1 render scale, disabled-state sibling images being incorrectly
included, and `GameObject.activeInHierarchy` being unreliable for un-instantiated prefab assets
(always false, even when active - see `RenderMetadataProbe.IsActiveUpToRoot`). Catalog now 65
entries. `SmokeTest.DumpImageStates` added as a reusable diagnostic for inspecting a prefab's
Image component states. See the implementation plan's
day-by-day task breakdown for what's next (T2.2 candidates.ts).

Fixture in use: Melon project, frame "Lose Screen" (`178:35186`), feature folder
`Assets/Textures/UI/UI Elements` (62 sprites) plus 3 explicit extra prefabs (`ButtonFrame`,
`ButtonFrameTint`, `Scrim` - see `-extraPrefabPaths` in `scripts/build-catalog.sh`), 65 catalog
entries total.
