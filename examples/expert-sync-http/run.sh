#!/usr/bin/env bash
# 同步非流式调用示例（需先启动支持 HTTP 的 Runtime）

set -euo pipefail

# 假设 Runtime HTTP 监听在 localhost:5000
curl -s -X POST http://localhost:5000/v1/run \
  -H "Content-Type: application/json" \
  -d '{
    "modelId": "deepseek-v4-pro",
    "input": "用一句话说明 Madorin Expert 模式最适合解决什么类型的问题。",
    "requestControl": {
      "temperature": 0.2,
      "maxTokens": 100
    }
  }' | jq .
