#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
RID="osx-arm64"
REBUILD=0
NO_BUMP=0
BUMP=patch
SKIP_PROVIDERS=0

usage() {
  echo "Usage: $0 [--rid osx-arm64|osx-x64] [--rebuild] [--no-bump] [--bump patch|minor|major] [--skip-providers]" >&2
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --rid)
      RID="${2:-}"
      shift 2
      ;;
    --rebuild)
      REBUILD=1
      shift
      ;;
    --no-bump)
      NO_BUMP=1
      shift
      ;;
    --bump)
      BUMP="${2:-}"
      shift 2
      ;;
    --skip-providers)
      SKIP_PROVIDERS=1
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage
      exit 1
      ;;
  esac
done

if [[ "$RID" != "osx-arm64" && "$RID" != "osx-x64" ]]; then
  echo "Unsupported RID '$RID'." >&2
  exit 1
fi

PS1="$SCRIPT_DIR/madorin-cli.publish.ps1"
if command -v pwsh >/dev/null 2>&1; then
  pwsh_args=(-NoProfile -File "$PS1" -Rid "$RID" -Bump "$BUMP")
  if [[ "$REBUILD" -eq 1 ]]; then
    pwsh_args+=(-Rebuild)
  fi
  if [[ "$NO_BUMP" -eq 1 ]]; then
    pwsh_args+=(-NoBump)
  fi
  if [[ "$SKIP_PROVIDERS" -eq 1 ]]; then
    pwsh_args+=(-SkipProviders)
  fi
  exec pwsh "${pwsh_args[@]}"
fi

echo "pwsh was not found; using native bash publish."
export MADORIN_PUBLISH_DEFAULT_RID="$RID"
export MADORIN_PUBLISH_RID_REGEX='^(osx-x64|osx-arm64)$'
export MADORIN_PUBLISH_HASH_CMD='shasum -a 256'
native_args=(--rid "$RID" --bump "$BUMP")
if [[ "$REBUILD" -eq 1 ]]; then
  native_args+=(--rebuild)
fi
if [[ "$NO_BUMP" -eq 1 ]]; then
  native_args+=(--no-bump)
fi
if [[ "$SKIP_PROVIDERS" -eq 1 ]]; then
  native_args+=(--skip-providers)
fi
exec bash "$SCRIPT_DIR/madorin-cli.publish.linux.sh" "${native_args[@]}"
