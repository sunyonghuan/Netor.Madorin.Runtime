# OpenAI DALL-E 图片生成 API

## 基本信息

- **厂商**: OpenAI
- **产品**: DALL-E 2 / DALL-E 3
- **端点**: `https://api.openai.com/v1/images/generations`
- **方法**: POST
- **文档**: https://platform.openai.com/docs/guides/image-generation
- **API 参考**: https://platform.openai.com/docs/api-reference/images/create

## 认证

```
Authorization: Bearer YOUR_API_KEY
```

## 请求参数

### 核心参数

| 参数 | 类型 | 必需 | 说明 | DALL-E 2 | DALL-E 3 |
|------|------|------|------|----------|----------|
| `prompt` | string | ✅ | 图片描述文本 | 最多 1000 字符 | 最多 4000 字符 |
| `model` | string | ✅ | 模型名称 | `dall-e-2` | `dall-e-3` |
| `n` | integer | ❌ | 生成图片数量 | 1-10 | 仅支持 1 |
| `size` | string | ❌ | 图片尺寸 | `256x256`, `512x512`, `1024x1024` | `1024x1024`, `1792x1024`, `1024x1792` |
| `quality` | string | ❌ | 图片质量 | 不支持 | `standard`, `hd` |
| `style` | string | ❌ | 风格 | 不支持 | `vivid`, `natural` |
| `response_format` | string | ❌ | 响应格式 | `url`, `b64_json` | `url`, `b64_json` |
| `user` | string | ❌ | 用户标识符 | ✅ | ✅ |

### 默认值

- `n`: 1
- `size`: `1024x1024`
- `quality`: `standard` (DALL-E 3)
- `style`: `vivid` (DALL-E 3)
- `response_format`: `url`

## 请求示例

```json
{
  "model": "dall-e-3",
  "prompt": "a white siamese cat",
  "n": 1,
  "size": "1024x1024",
  "quality": "standard",
  "style": "vivid"
}
```

## 响应格式

```json
{
  "created": 1702839645,
  "data": [
    {
      "url": "https://oaidalleapiprodscus.blob.core.windows.net/...",
      "revised_prompt": "A fluffy white Siamese cat with bright blue eyes..."
    }
  ]
}
```

### 响应字段

| 字段 | 类型 | 说明 |
|------|------|------|
| `created` | integer | Unix 时间戳 |
| `data` | array | 生成结果数组 |
| `data[].url` | string | 图片 URL（60 分钟有效） |
| `data[].b64_json` | string | Base64 编码的图片（仅当 `response_format=b64_json`） |
| `data[].revised_prompt` | string | DALL-E 3 修订后的提示词 |

## 错误码

| HTTP 状态码 | 错误码 | 说明 |
|-------------|--------|------|
| 400 | invalid_request_error | 参数错误 |
| 401 | authentication_error | API Key 无效 |
| 429 | rate_limit_exceeded | 请求频率超限 |
| 500 | api_error | 服务端错误 |

## 公共参数抽取

### 图片生成通用参数

- ✅ `prompt` (文本提示词)
- ✅ `size` / `width` + `height` (尺寸)
- ✅ `quality` (质量等级)
- ✅ `n` / `num_images` (生成数量)
- ✅ `style` (风格预设)
- ❌ `seed` (随机种子 - OpenAI 不支持)
- ❌ `negative_prompt` (负面提示词 - OpenAI 不支持)

## 特殊能力

- DALL-E 3 会自动优化提示词（返回 `revised_prompt`）
- 图片 URL 60 分钟后失效，需及时下载
- 不支持自定义种子（无法精确复现）
- 不支持负面提示词

## 定价（参考）

**DALL-E 3**:
- `1024x1024` standard: $0.040 / 图
- `1024x1024` HD: $0.080 / 图
- `1792x1024` / `1024x1792` standard: $0.080 / 图
- `1792x1024` / `1024x1792` HD: $0.120 / 图

**DALL-E 2**:
- `1024x1024`: $0.020 / 图
- `512x512`: $0.018 / 图
- `256x256`: $0.016 / 图
