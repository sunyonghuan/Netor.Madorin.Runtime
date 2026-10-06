#!/usr/bin/env bash
# Expert 模式 JSON 结构化输出示例

set -euo pipefail

echo "=== Expert JSON Output Example ==="

madorin run \
  --response-format json \
  --input "分析当前项目的 README.md（如果存在）或 package.json / *.csproj，提取项目名称、主要特性（3-5 条）、使用的核心技术栈。以 JSON 格式返回，字段为 projectName, features[], techStack[]。" \
  "$@"
