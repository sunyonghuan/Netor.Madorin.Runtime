#!/usr/bin/env bash
# 高创造力生成示例

set -euo pipefail

madorin run \
  --temperature 0.95 \
  --top-p 0.95 \
  --input "为一个名为『星尘笔记』的极简生产力 App 写一段 180 字以内的宣传文案，要求富有诗意且突出『捕捉灵感碎片』的理念。" \
  "$@"
