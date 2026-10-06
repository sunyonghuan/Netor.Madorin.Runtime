#!/usr/bin/env bash
# 计划功能演示（交互式）

set -euo pipefail

echo "=== Expert Planning Example ==="
echo "请在交互界面中尝试让 Agent 使用 mcli_expert_create_plan 创建计划。"
echo "示例输入："
echo "  '为项目添加用户偏好设置功能（主题、通知、语言），请使用计划来拆解和执行。'"

madorin run "$@"
