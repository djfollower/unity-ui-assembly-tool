# unity-ui-assembly-tool

Unity UI Assembly Tool: Mockup to Prefab

A vertical slice proving the pipeline (Figma frame -> reduced element tree -> matched against a
Unity asset catalog -> assembled prefab) on one real screen, against two accuracy gates, before
committing to the full system. The three-week slice is now complete end-to-end - see "Status"
below.

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
`Packages/manifest.json` (`"com.dungphan.ui-assembler.editor": "file:../../unity-ui-assembly-tool/packages/unity-editor"`).

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

## Full flow (Figma frame -> assembled prefab)

Four stages, each handing off through a JSON file in `.cache/` (or `Assets/_Generated/` for the
final prefab) - no stage calls another directly. Run them in order:

**1. Build the Unity asset catalog** (only needs re-running when the target project's UI art
changes):

```
scripts/build-catalog.sh
```

Runs Unity in `-batchmode` against the target project (`PROJECT_PATH`, default `/Users/dungphan/Melon`),
discovering every sprite/prefab in the feature folder plus any `-extraPrefabPaths`, probing each
one's render metadata (`RenderMetadataProbe`), and rendering a canonical thumbnail for each
(`RenderedThumbnail`) - see that file's doc comment for why thumbnails go through a real
Canvas/Camera render rather than a hand-rolled compositor. Writes `.cache/catalog.json`.

Incremental by default: each asset's `AssetDatabase.GetAssetDependencyHash` is cached in
`.cache/catalog-build-cache.json`, so a re-run only re-probes/re-renders assets that actually
changed (or whose dependencies changed) - unchanged entries are reused verbatim. Set
`FORCE_FULL=true` to bypass the cache and re-render everything (needed after changing
`RenderMetadataProbe.cs`/`RenderedThumbnail.cs` themselves, a change the content hash can't see).

Thumbnails are separate PNG files under `.cache/thumbnails/`, not inline base64 in `catalog.json`
(`thumbnail_path` is a path relative to catalog.json's own directory) - keeps the catalog itself
small and diffable even at thousands of entries. Both `loadCatalog` (Node) and
`AssemblerJson.LoadCatalog` (Unity) resolve it to an absolute path on load, so nothing downstream
needs to know where catalog.json lives.

**2. Capture the Figma frame and reduce it to an element tree**:

```
npm run cli --workspace=@ui-assembler-slice/mcp-tool -- reduce
```

Reads `FIGMA_FILE_KEY`/`FIGMA_NODE_ID` from `.env`, pulls the frame (from the Figma-plugin cache
first, see "Figma access" above), parses it into a raw layer tree, reduces it via the agent
adapter (drops decorative noise, names/types the surviving elements), and normalizes it to
`.cache/element-tree.json`. If you're working from the Figma plugin's exported selection instead
of a live frame fetch, use `reduce-from-selection <plugin-export.json>` instead - deterministic,
no agent call, no network.

**3. Match the element tree against the catalog**:

```
npm run cli --workspace=@ui-assembler-slice/mcp-tool -- match .cache/element-tree.json .cache/catalog.json
```

Tiered candidate retrieval (`candidates.ts`) -> structural + visual scoring signals -> an
auto-accept/reject gate (`gate.ts`), recursing into composite elements' children. Writes
`.cache/match-result.json` (each element tagged `matched` / `uncertain` / `missing`).

**4. Assemble the prefab in Unity**:

```
"/Applications/Unity/Hub/Editor/<version>/Unity.app/Contents/MacOS/Unity" \
  -batchmode -projectPath <path-to-target-project> \
  -executeMethod UiAssemblerSlice.Editor.Batch.RunAssemble.Run \
  -elementTreePath <path>/.cache/element-tree.json \
  -matchResultPath <path>/.cache/match-result.json \
  -quit -logFile <path>
```

`CanvasScaffold` builds the root Canvas from the element tree's `canvas_reference` config,
`NodeBuilder` recursively builds each matched element (resizing/re-slicing catalog assets to fit,
applying tints, laying out text), `PrefabWriter` saves the result. Defaults to
`Assets/_Generated/UIAssembler/<frameId>.prefab` (`:` sanitized to `_`) unless `-outputPath` is
given.

**Scoring against fixtures** (validates the pipeline, not required for a normal run):

```
python3 scoring/score_gate1.py .cache/element-tree.json      # element recall/precision/hierarchy
python3 scoring/score_gate2.py .cache/match-result.json       # FAR, auto-accept rate, missing-recall
```

## Status

The vertical slice is **complete end-to-end**: Gate 1 PASS, Gate 2 PASS (see `scoring/report.md`
for the full readout and go/no-go), and the Week 3 assembler produces a real `.prefab` from the
"Lose Screen" fixture, reviewed live in the Editor against the Figma mockup by the user. See
`HANDOFF.md` for the current state in detail - what's been fixed, what's still open, and the
non-obvious operational facts (Figma quota, agent-adapter cost, Unity rendering gotchas) that
aren't obvious from the code alone.

Fixture in use: Melon project, frame "Lose Screen" (`178:35186`), feature folder
`Assets/Textures/UI/UI Elements` (62 sprites) plus 3 explicit extra prefabs (`ButtonFrame`,
`ButtonFrameTint`, `Scrim` - see `-extraPrefabPaths` in `scripts/build-catalog.sh`), 65 catalog
entries total.
