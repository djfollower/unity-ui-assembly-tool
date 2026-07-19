#!/usr/bin/env bash
# T3.3: runs RunAssemble.cs against the target Unity project in batch mode
# and writes an assembled .prefab. Mirrors build-catalog.sh's conventions -
# see README.md for why -nographics is deliberately omitted.
set -euo pipefail

UNITY_APP="${UNITY_APP:-/Applications/Unity/Hub/Editor/2022.3.62f2/Unity.app/Contents/MacOS/Unity}"
PROJECT_PATH="${PROJECT_PATH:-/Users/dungphan/Melon}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# fixtures/golden-elements.json stands in for a produced element-tree.json
# (same convention already documented in HANDOFF.md for the matcher).
ELEMENT_TREE_PATH="${ELEMENT_TREE_PATH:-$REPO_ROOT/fixtures/golden-elements.json}"
MATCH_RESULT_PATH="${MATCH_RESULT_PATH:-$REPO_ROOT/.cache/match-result.json}"
CATALOG_PATH="${CATALOG_PATH:-$REPO_ROOT/.cache/catalog.json}"
# Left unset by default so RunAssemble.cs computes Assets/_Generated/UIAssembler/<frame_id>.prefab
OUTPUT_PATH="${OUTPUT_PATH:-}"
LOG_PATH="${LOG_PATH:-$REPO_ROOT/scripts/logs/assemble.log}"

mkdir -p "$(dirname "$LOG_PATH")"

EXTRA_ARGS=()
if [[ -n "$OUTPUT_PATH" ]]; then
  EXTRA_ARGS+=(-outputPath "$OUTPUT_PATH")
fi

"$UNITY_APP" \
  -batchmode \
  -projectPath "$PROJECT_PATH" \
  -executeMethod UiAssemblerSlice.Editor.Batch.RunAssemble.Run \
  -elementTreePath "$ELEMENT_TREE_PATH" \
  -matchResultPath "$MATCH_RESULT_PATH" \
  -catalogPath "$CATALOG_PATH" \
  "${EXTRA_ARGS[@]+"${EXTRA_ARGS[@]}"}" \
  -quit \
  -logFile "$LOG_PATH"

echo "log: $LOG_PATH"
