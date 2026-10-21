#!/usr/bin/env bash
set -euo pipefail
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
if ! command -v pwsh >/dev/null 2>&1; then
    printf '%s\n' 'PowerShell 7 (pwsh) is required. Install it before publishing.' >&2
    exit 127
fi
exec pwsh -NoLogo -NoProfile -File "$script_dir/publish.ps1" "$@"
