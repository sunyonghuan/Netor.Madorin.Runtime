#!/usr/bin/env bash
set -e
rid="${MADORIN_SKILL_RID:-linux-x64}"
exec "$(dirname "$0")/../runtime/$rid/skill-gemini-image" "$@"
