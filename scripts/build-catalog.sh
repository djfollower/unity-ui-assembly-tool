#!/usr/bin/env bash
# T1.6: runs RunCatalogBuild.cs against the target Unity project in batch
# mode and writes catalog.json. See README.md for why -nographics is
# deliberately omitted (it breaks thumbnail rendering on this machine).
set -euo pipefail

UNITY_APP="${UNITY_APP:-/Applications/Unity/Hub/Editor/2022.3.62f2/Unity.app/Contents/MacOS/Unity}"
PROJECT_PATH="${PROJECT_PATH:-/Users/dungphan/Melon}"
FEATURE_FOLDER="${FEATURE_FOLDER:-Assets/Textures/UI/UI Elements}"
# Explicit opt-in prefabs outside the feature folder, added as fixture test
# cases (ButtonFrameTint covers Gate 2's tinted-asset requirement). See
# RunCatalogBuild.cs's comment on why this isn't a whole-folder scan.
EXTRA_PREFAB_PATHS="${EXTRA_PREFAB_PATHS:-Assets/Prefabs/UI/ButtonFrame.prefab,Assets/Prefabs/UI/ButtonFrameTint.prefab,Assets/Prefabs/UI/Scrim.prefab}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUTPUT_PATH="${OUTPUT_PATH:-$REPO_ROOT/.cache/catalog.json}"
LOG_PATH="${LOG_PATH:-$REPO_ROOT/scripts/logs/build-catalog.log}"

mkdir -p "$(dirname "$OUTPUT_PATH")" "$(dirname "$LOG_PATH")"

"$UNITY_APP" \
  -batchmode \
  -projectPath "$PROJECT_PATH" \
  -executeMethod UiAssemblerSlice.Editor.Batch.RunCatalogBuild.Run \
  -featureFolder "$FEATURE_FOLDER" \
  -extraPrefabPaths "$EXTRA_PREFAB_PATHS" \
  -outputPath "$OUTPUT_PATH" \
  -quit \
  -logFile "$LOG_PATH"

echo "catalog.json: $OUTPUT_PATH"
echo "log: $LOG_PATH"
