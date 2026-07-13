# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A vertical slice proving a pipeline: **Figma frame → reduced element tree → matched against a
Unity asset catalog → assembled prefab**, validated against two hand-labeled accuracy gates,
before committing to the full system. See `HANDOFF.md` for current state (what's done, what's
next, non-obvious operational facts) — check it first when resuming work here.

## Commands

```
npm install
npm run build   # generates contracts/src/types.ts from JSON Schemas, validates schemas
                 # against examples, typechecks contracts + mcp-tool
```

Per-workspace:
```
npm run build --workspace=@ui-assembler-slice/contracts   # generate + validate-schemas + typecheck
npm run build --workspace=@ui-assembler-slice/mcp-tool     # typecheck only
npm run cli --workspace=@ui-assembler-slice/mcp-tool       # tsx src/cli.ts <command>
```

Build the Unity asset catalog (writes `.cache/catalog.json`):
```
scripts/build-catalog.sh
```
Runs Unity in `-batchmode` against `PROJECT_PATH` (default `/Users/dungphan/Melon`) invoking
`UiAssemblerSlice.Editor.Batch.RunCatalogBuild.Run`. All paths are env-overridable (see the
script). **Never pass `-nographics`** for anything that renders (thumbnails) — it silently
returns flat/uninitialized `RenderTexture` output on this machine. Only one Unity instance can
hold the target project open at a time; batch mode fails fast (exit 134) if the Editor is already
open on it.

Scoring against fixtures:
```
python3 scoring/score_gate1.py   # element recall/precision/hierarchy vs fixtures/golden-elements.json
```
`scoring/score_gate2.py` (FAR, auto-accept rate, missing-recall) doesn't exist yet — Week 2 task.

There is no test suite yet; correctness is currently verified by running the pipeline against the
fixtures and scoring the output, not unit tests.

## Architecture

Two processes that **never call each other directly** — they hand off through JSON files
validated against `packages/contracts/schemas/`:

- `packages/mcp-tool/` — Node/TS. Figma adapter, LLM reduction, matcher. No MCP server wrapper
  yet for the slice (`src/cli.ts` is a bare subcommand dispatcher invoked directly).
- `packages/unity-editor/` — C#. A local UPM package (`com.ui-assembler-slice.editor`) consumed
  by the target Unity project via `Packages/manifest.json`
  (`"com.ui-assembler-slice.editor": "file:../../unity-ui-assembly-tool/packages/unity-editor"`).
  Batch-mode entry points only (`Editor/Batch/`); no in-Editor UI for this slice.
- `packages/contracts/` — shared JSON Schemas (`schemas/*.schema.json`) are the **source of
  truth**; `src/generated/*.ts` is generated from them via `json-schema-to-typescript` — never
  hand-edit generated files, edit the schema and run `npm run generate`.

### Pipeline stages (mcp-tool)

`src/figma/`: `fetch-frame.ts` → `parse-tree.ts` → `reduce.ts` → `normalize.ts` — pulls a Figma
frame, parses it into a raw layer tree, uses an LLM to reduce it to meaningful UI elements
(dropping decorative noise), normalizes to the `element-tree` schema.

`src/matcher/` (Week 2, in progress — files are currently stubs): `candidates.ts` (tiered
retrieval pre-filter) → `visual-signal.ts` + `structural-signal.ts` (scoring signals) →
`gate.ts` (auto-accept/reject threshold decision) — matches reduced elements against
`.cache/catalog.json` catalog entries.

`packages/unity-editor/Editor/`: `Catalog/AssetDiscovery.cs` (finds catalog-eligible assets) →
`Catalog/RenderMetadataProbe.cs` + `RenderedThumbnail.cs` (extract render metadata, render
thumbnails) produce `catalog.json`; `Assembler/CanvasScaffold.cs` → `NodeBuilder.cs` →
`PrefabWriter.cs` (Week 3, not started) will consume `match-result.json` and build the actual
prefab.

### Key non-obvious facts (see `HANDOFF.md` for the full, current list)

- **Figma REST API is capped at 6 requests/month** (free tier). The primary path is
  `packages/mcp-tool/figma-plugin/` (runs inside Figma, not metered) writing to
  `.cache/figma/<fileKey>/<nodeId>.json`; `fetch-frame.ts` reads that cache first always and only
  hits the REST API when explicitly forced.
- **`reduce.ts` and `visual-signal.ts` both go through `packages/mcp-tool/src/agent/`**, an
  agent-CLI adapter that shells out to the `claude` CLI and reuses this environment's
  authenticated login — no `ANTHROPIC_API_KEY` needed. Dev-time convenience (the CLI needs an
  interactive login, so it can't run fully unattended in CI yet); the adapter interface is
  designed to be agent-agnostic — add a new coding-agent CLI by implementing `AgentAdapter` and
  registering it in `registry.ts`, without touching the two call sites.
- **`GameObject.activeInHierarchy` is unreliable on prefab assets** loaded via `AssetDatabase`
  outside a scene (always reports `false`). Use `RenderMetadataProbe.IsActiveUpToRoot` instead.
- Composite button prefabs can have several `Image` components (decorative frame, disabled-state
  siblings, sprite-less placeholders); `RenderMetadataProbe.ResolveMainImage` resolves the one a
  `Selectable`'s `targetGraphic` points to (filtered to visually-contributing images), falling
  back to largest-by-area. `SmokeTest.DumpImageStates` is the diagnostic for debugging this.
- The fixture/target Unity project (`/Users/dungphan/Melon`) is a separate, real game repo — ask
  the user directly for facts about it rather than exploring it, and don't spawn subagents into it.
