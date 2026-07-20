#!/usr/bin/env bash
# T1.6: runs RunCatalogBuild.cs against the target Unity project in batch
# mode and writes catalog.json. See README.md for why -nographics is
# deliberately omitted (it breaks thumbnail rendering on this machine).
set -euo pipefail

UNITY_APP="${UNITY_APP:-/Applications/Unity/Hub/Editor/2022.3.62f2/Unity.app/Contents/MacOS/Unity}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$REPO_ROOT/scripts/lib/detect-project-path.sh"
# Auto-detected from the UPM manifest of whichever sibling Unity project has
# this package installed - set PROJECT_PATH explicitly to override (e.g. if
# more than one project references this package).
if [ -z "${PROJECT_PATH:-}" ]; then
  PROJECT_PATH="$(detect_project_path "$REPO_ROOT")" || exit 1
fi
FEATURE_FOLDER="${FEATURE_FOLDER:-Assets/Textures/UI/UI Elements}"
# Explicit opt-in prefabs outside the feature folder, added as fixture test
# cases (ButtonFrameTint covers Gate 2's tinted-asset requirement). See
# RunCatalogBuild.cs's comment on why this isn't a whole-folder scan.
EXTRA_PREFAB_PATHS="${EXTRA_PREFAB_PATHS:-Assets/Prefabs/UI/ButtonFrame.prefab,Assets/Prefabs/UI/ButtonFrameTint.prefab,Assets/Prefabs/UI/Scrim.prefab}"
# Project-wide alternative to FEATURE_FOLDER, for catalog-eligible sprites
# scattered across dozens of unrelated folders with no common parent - takes
# priority over FEATURE_FOLDER when set. Sprites need this Unity Asset Label
# applied first (see MarkCatalogEligible.cs / Project window right-click >
# UI Assembler > Mark Sprites As Catalog-Eligible). Unset by default - the
# fixture project has no scattered-folder problem, so folder mode stays the
# default.
LABEL_FILTER="${LABEL_FILTER:-}"
OUTPUT_PATH="${OUTPUT_PATH:-$REPO_ROOT/.cache/catalog.json}"
CACHE_PATH="${CACHE_PATH:-$REPO_ROOT/.cache/catalog-build-cache.json}"
# Skips the incremental build cache entirely, re-probing/re-rendering every
# asset - set FORCE_FULL=true after changing RunCatalogBuild.cs/RenderMetadataProbe.cs/
# RenderedThumbnail.cs themselves (a code change the cache's per-asset
# content hash can't see), or if the cache is ever suspected stale.
FORCE_FULL="${FORCE_FULL:-false}"
LOG_PATH="${LOG_PATH:-$REPO_ROOT/scripts/logs/build-catalog.log}"

mkdir -p "$(dirname "$OUTPUT_PATH")" "$(dirname "$LOG_PATH")"

EXTRA_ARGS=()
if [[ -n "$LABEL_FILTER" ]]; then
  EXTRA_ARGS+=(-labelFilter "$LABEL_FILTER")
fi

"$UNITY_APP" \
  -batchmode \
  -projectPath "$PROJECT_PATH" \
  -executeMethod UiAssemblerSlice.Editor.Batch.RunCatalogBuild.Run \
  -featureFolder "$FEATURE_FOLDER" \
  -extraPrefabPaths "$EXTRA_PREFAB_PATHS" \
  -outputPath "$OUTPUT_PATH" \
  -cachePath "$CACHE_PATH" \
  -forceFull "$FORCE_FULL" \
  "${EXTRA_ARGS[@]+"${EXTRA_ARGS[@]}"}" \
  -quit \
  -logFile "$LOG_PATH"

echo "catalog.json: $OUTPUT_PATH"
echo "build cache: $CACHE_PATH"
echo "log: $LOG_PATH"
