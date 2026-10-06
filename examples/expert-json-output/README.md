# Expert 模式 - JSON 结构化输出示例

本示例演示如何让 Expert 模式以严格的 JSON 格式返回结果。

## 适用场景

- 需要机器可解析的结构化输出
- 后续流程要消费 JSON（CI、脚本、数据库导入等）
- 配合 `RequestControl.ResponseFormat = "json"`

## 使用方式

### CLI

```bash
madorin run \
  --response-format json \
  --input "请分析当前目录下的 README.md，提取项目名称、主要功能点（3-5 个），以及依赖的外部服务。以 JSON 返回，字段为 name, features[], externalServices[]"
```

### 带 Schema 的结构化输出（部分 Provider 支持）

```bash
madorin run \
  --response-format json \
  --input "..." 
# 更严格的 Schema 控制通常通过 Agent 的 System Prompt + RequestControl 组合实现
```

## 示例 Schema（schema.json）

```json
{
  "type": "object",
  "properties": {
    "projectName": { "type": "string" },
    "features": {
      "type": "array",
      "items": { "type": "string" }
    },
    "externalServices": {
      "type": "array",
      "items": { "type": "string" }
    }
  },
  "required": ["projectName", "features", "externalServices"]
}
```

## 注意事项

- 并非所有 Provider 对 `ResponseFormat=json` 的支持程度相同。
- 建议在 Prompt 中明确描述 JSON 结构。
- 使用 `--no-stream` 可获得完整 JSON 后再进行后续处理。

## 验证

运行后可用 `jq` 或其他工具验证是否为合法 JSON：

```bash
madorin run --no-stream --response-format json --input "..." | jq .
```
