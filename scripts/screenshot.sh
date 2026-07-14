#!/usr/bin/env bash
# Runs RunScreenshot.cs against the target Unity project in batch mode and
# writes a rendered PNG of the assembled hierarchy at canvas_reference
# resolution, for an automated comparison against fixtures/frame-export.png.
# Mirrors assemble.sh's conventions - see README.md for why -nographics is
# deliberately omitted (it silently produces blank/uninitialized render
# output on this machine).
set -euo pipefail

UNITY_APP="${UNITY_APP:-/Applications/Unity/Hub/Editor/2022.3.62f2/Unity.app/Contents/MacOS/Unity}"
PROJECT_PATH="${PROJECT_PATH:-/Users/dungphan/Melon}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# fixtures/golden-elements.json stands in for a produced element-tree.json
# (same convention already documented in HANDOFF.md for the matcher/assembler).
ELEMENT_TREE_PATH="${ELEMENT_TREE_PATH:-$REPO_ROOT/fixtures/golden-elements.json}"
MATCH_RESULT_PATH="${MATCH_RESULT_PATH:-$REPO_ROOT/.cache/match-result.json}"
CATALOG_PATH="${CATALOG_PATH:-$REPO_ROOT/.cache/catalog.json}"
OUTPUT_PATH="${OUTPUT_PATH:-$REPO_ROOT/.cache/rendered-frame.png}"
BG_COLOR="${BG_COLOR:-}"
LOG_PATH="${LOG_PATH:-$REPO_ROOT/scripts/logs/screenshot.log}"

mkdir -p "$(dirname "$OUTPUT_PATH")" "$(dirname "$LOG_PATH")"

EXTRA_ARGS=()
if [[ -n "$BG_COLOR" ]]; then
  EXTRA_ARGS+=(-bgColor "$BG_COLOR")
fi

"$UNITY_APP" \
  -batchmode \
  -projectPath "$PROJECT_PATH" \
  -executeMethod UiAssemblerSlice.Editor.Batch.RunScreenshot.Run \
  -elementTreePath "$ELEMENT_TREE_PATH" \
  -matchResultPath "$MATCH_RESULT_PATH" \
  -catalogPath "$CATALOG_PATH" \
  -outputPath "$OUTPUT_PATH" \
  "${EXTRA_ARGS[@]+"${EXTRA_ARGS[@]}"}" \
  -quit \
  -logFile "$LOG_PATH"

echo "rendered-frame.png: $OUTPUT_PATH"
echo "log: $LOG_PATH"
