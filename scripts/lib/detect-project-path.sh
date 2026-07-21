# Auto-detects the Unity project that has this repo's Editor package
# installed via UPM, so PROJECT_PATH doesn't have to be hand-maintained
# per script/per machine. The UPM manifest is already the single source of
# truth for "which project uses this package" - packages/unity-editor is
# consumed via an entry in the target project's own Packages/manifest.json
# keyed by "com.dungphan.ui-assembler.editor", either a local `file:`
# reference (e.g. "file:../../unity-ui-assembly-tool/packages/unity-editor",
# resolves correctly only when the project is a sibling directory of this
# repo - same layout ReviewWindow.cs's own RepoRoot assumes in the opposite
# direction) or a git URL (e.g.
# "https://github.com/.../unity-ui-assembly-tool.git?path=packages/unity-editor#v0.1.2").
# A `file:` value is verified by resolving it back to this repo's own
# package_dir; any other value (git URL, registry) is trusted on the key
# match alone - there's no local path to cross-check in that case.
#
# Usage:
#   REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
#   source "$REPO_ROOT/scripts/lib/detect-project-path.sh"
#   if [ -z "${PROJECT_PATH:-}" ]; then
#     PROJECT_PATH="$(detect_project_path "$REPO_ROOT")" || exit 1
#   fi
detect_project_path() {
  local repo_root="$1"
  local package_dir manifest proj_dir value rel_path resolved
  local matches=()

  package_dir="$(cd "$repo_root/packages/unity-editor" && pwd)"

  while IFS= read -r manifest; do
    value=$(grep -o '"com\.dungphan\.ui-assembler\.editor"[[:space:]]*:[[:space:]]*"[^"]*"' "$manifest" 2>/dev/null \
      | sed -E 's/.*:[[:space:]]*"([^"]*)"/\1/')
    [ -z "$value" ] && continue

    proj_dir="$(dirname "$(dirname "$manifest")")"
    if [[ "$value" == file:* ]]; then
      rel_path="${value#file:}"
      resolved="$(cd "$proj_dir/Packages/$rel_path" 2>/dev/null && pwd || true)"
      [ "$resolved" != "$package_dir" ] && continue
    fi
    matches+=("$proj_dir")
  done < <(find "$(dirname "$repo_root")" -maxdepth 3 -path "*/Packages/manifest.json" 2>/dev/null)

  case "${#matches[@]}" in
    0)
      echo "detect_project_path: no Unity project under $(dirname "$repo_root") references" \
        "com.dungphan.ui-assembler.editor from $package_dir - add the UPM dependency first," \
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
