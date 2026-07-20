# Auto-detects the Unity project that has this repo's Editor package
# installed via UPM, so PROJECT_PATH doesn't have to be hand-maintained
# per script/per machine. The UPM manifest is already the single source of
# truth for "which project uses this package" - packages/unity-editor is
# consumed via a local `file:` reference in the target project's own
# Packages/manifest.json (e.g. "com.ui-assembler-slice.editor":
# "file:../../unity-ui-assembly-tool/packages/unity-editor"), which only
# resolves correctly when the project is a sibling directory of this repo -
# same layout ReviewWindow.cs's own RepoRoot assumes in the opposite
# direction (Application.dataPath -> repo root).
#
# Usage:
#   REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
#   source "$REPO_ROOT/scripts/lib/detect-project-path.sh"
#   if [ -z "${PROJECT_PATH:-}" ]; then
#     PROJECT_PATH="$(detect_project_path "$REPO_ROOT")" || exit 1
#   fi
detect_project_path() {
  local repo_root="$1"
  local package_dir manifest proj_dir rel_path resolved
  local matches=()

  package_dir="$(cd "$repo_root/packages/unity-editor" && pwd)"

  while IFS= read -r manifest; do
    rel_path=$(grep -o '"com\.ui-assembler-slice\.editor"[[:space:]]*:[[:space:]]*"file:[^"]*"' "$manifest" 2>/dev/null \
      | sed -E 's/.*"file:([^"]*)".*/\1/')
    [ -z "$rel_path" ] && continue

    proj_dir="$(dirname "$(dirname "$manifest")")"
    resolved="$(cd "$proj_dir/Packages/$rel_path" 2>/dev/null && pwd || true)"
    if [ "$resolved" = "$package_dir" ]; then
      matches+=("$proj_dir")
    fi
  done < <(find "$(dirname "$repo_root")" -maxdepth 3 -path "*/Packages/manifest.json" 2>/dev/null)

  case "${#matches[@]}" in
    0)
      echo "detect_project_path: no Unity project under $(dirname "$repo_root") references" \
        "com.ui-assembler-slice.editor from $package_dir - add the UPM dependency first," \
        "or set PROJECT_PATH explicitly." >&2
      return 1
      ;;
    1)
      echo "${matches[0]}"
      ;;
    *)
      echo "detect_project_path: multiple Unity projects reference this package - set PROJECT_PATH explicitly:" >&2
      printf '  %s\n' "${matches[@]}" >&2
      return 1
      ;;
  esac
}
