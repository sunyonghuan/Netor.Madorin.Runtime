#!/usr/bin/env zsh
set -euo pipefail
root="${0:A:h}/.."
bridge="${root}/mcp/bin/mcp-bridge.mjs"
if [[ ! -f "$bridge" ]]; then
  print -u2 -- "Error: bridge not found at \"$bridge\""
  exit 2
fi
exec node "$bridge" "$@"
