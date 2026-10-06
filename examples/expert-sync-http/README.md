# Expert 模式 - 非流式同步 HTTP 调用示例

本示例展示如何使用 `/v1/run` 同步端点获取完整结果，而非流式事件。

## 适用场景

- 脚本 / CI 自动化
- 需要拿到完整结果后再进行后续处理
- 简单集成，不想处理 SSE 事件流

## 调用方式

### 使用 curl

```bash
curl -X POST http://localhost:5000/v1/run \
  -H "Content-Type: application/json" \
  -d '{
    "modelId": "deepseek-v4-pro",
    "input": "请用一句话总结 Madorin 的核心价值。",
    "requestControl": {
      "temperature": 0.3,
      "maxTokens": 200
    }
  }'
```

响应示例：

```json
{
  "runId": "xxx",
  "sessionId": "yyy",
  "output": "Madorin 是一个支持子 Agent 委派与小型计划的 AI Runtime。",
  "status": "Completed",
  "elapsedMs": 1240,
  "inputTokens": 45,
  "outputTokens": 28
}
```

### 超时控制

```bash
curl ... -H "X-Timeout-Seconds: 60"
```

或在 requestControl 中设置：

```json
"requestControl": { "timeoutMs": 60000 }
```

## 注意事项

- 同步接口内部仍然会驱动完整的 Runtime Run 循环（多轮 + 工具）。
- 超时默认 300 秒，可通过 RequestControl 或 Header 覆盖。
- 超时返回 HTTP 504。
