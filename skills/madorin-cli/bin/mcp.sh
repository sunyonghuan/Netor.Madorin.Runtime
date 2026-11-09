#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
bridge="$root/mcp/bin/mcp-bridge.mjs"
if [[ ! -f "$bridge" ]]; then
  echo "Error: bridge not found at \"$bridge\"" >&2
  exit 2
fi
exec node "$bridge" "$@"
