#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
RID="osx-arm64"
REBUILD=0
NO_BUMP=0
BUMP=patch
VERSION=""
SKIP_PROVIDERS=0

POSITIONAL=()

usage() {
  echo "Usage: $0 [mac|osx-arm64|list] [--rid osx-arm64] [--version x.y.z] [--rebuild] [--no-bump] [--bump patch|minor|major] [--skip-providers]" >&2
  echo "Example: $0 --version 1.2.3" >&2
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --rid)
      RID="${2:-}"
      shift 2
      ;;
    --)
      shift
      POSITIONAL+=("$@")
      break
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
    --version)
      if [[ $# -lt 2 || -z "$2" ]]; then
        echo "--version requires a value such as 1.2.3." >&2
        exit 1
      fi
      VERSION="$2"
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
      POSITIONAL+=("$1")
      shift
      ;;
  esac
done

if [[ "$RID" != "osx-arm64" ]]; then
  echo "Unsupported RID '$RID'. Cross publish supports osx-arm64." >&2
  exit 1
fi

if [[ -n "$VERSION" && ! "$VERSION" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]]; then
  echo "Invalid version '$VERSION'. Expected Major.Minor.Patch such as 1.2.3." >&2
  exit 1
fi

PS1="$SCRIPT_DIR/madorin-cli.publish.ps1"
if command -v pwsh >/dev/null 2>&1; then
  pwsh_args=(-NoProfile -File "$PS1" -Rid "$RID" -Bump "$BUMP")
  if [[ -n "$VERSION" ]]; then
    pwsh_args+=(-Version "$VERSION")
  fi
  if [[ ${#POSITIONAL[@]} -gt 0 ]]; then
    pwsh_args+=("${POSITIONAL[@]}")
  fi
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
if [[ ${#POSITIONAL[@]} -gt 0 ]]; then
  echo "Native bash publish only supports osx-arm64. Install pwsh to select platforms from one command." >&2
  exit 1
fi
export MADORIN_PUBLISH_DEFAULT_RID="$RID"
export MADORIN_PUBLISH_RID_REGEX='^(osx-arm64)$'
export MADORIN_PUBLISH_HASH_CMD='shasum -a 256'
native_args=(--rid "$RID" --bump "$BUMP")
if [[ -n "$VERSION" ]]; then
  native_args+=(--version "$VERSION")
fi
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
