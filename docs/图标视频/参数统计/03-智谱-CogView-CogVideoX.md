# 智谱 GLM CogView / CogVideoX API

## 基本信息

- **厂商**: 智谱 AI (Zhipu AI)
- **产品**: CogView-4 (图片), CogVideoX (视频)
- **文档**: https://open.bigmodel.cn/dev/api/image-generation

## 图片生成 API (CogView-4)

### 端点

```
POST https://open.bigmodel.cn/api/paas/v4/images/generations
```

### 认证

```
Authorization: Bearer YOUR_API_KEY
```

### 请求参数

| 参数 | 类型 | 必需 | 说明 | 默认值 |
|------|------|------|------|--------|
| `model` | string | ✅ | 模型名称 | - |
| `prompt` | string | ✅ | 图片描述文本 | - |
| `size` | string | ❌ | 图片尺寸 | `1024x1024` |
| `n` | integer | ❌ | 生成图片数量 | 1 |
| `quality` | string | ❌ | 图片质量 | `standard` |
| `response_format` | string | ❌ | 响应格式 | `url` |
| `user` | string | ❌ | 用户标识符 | - |

### 支持的模型

- `cogview-4`: CogView-4 主模型
- `cogview-3-plus`: CogView-3+ 模型

### 支持的尺寸

- `1024x1024`
- `768x1024`
- `1024x768`

### 支持的质量

- `standard`: 标准质量
- `hd`: 高清质量

### 请求示例

```json
{
  "model": "cogview-4",
  "prompt": "一只可爱的熊猫在竹林中玩耍",
  "size": "1024x1024",
  "n": 1,
  "quality": "standard"
}
```

### 响应格式

```json
{
  "created": 1702839645,
  "data": [
    {
      "url": "https://sfile.chatglm.cn/...",
      "b64_json": null
    }
  ]
}
```

## 视频生成 API (CogVideoX)

### 端点

```
POST https://open.bigmodel.cn/api/paas/v4/videos/generations
```

### 认证

```
Authorization: Bearer YOUR_API_KEY
```

### 请求参数

| 参数 | 类型 | 必需 | 说明 | 默认值 |
|------|------|------|------|--------|
| `model` | string | ✅ | 模型名称 | - |
| `prompt` | string | ✅ | 视频描述文本 | - |
| `image_url` | string | ❌ | 参考图片 URL（图生视频） | - |
| `duration` | integer | ❌ | 视频时长（秒） | 5 |
| `fps` | integer | ❌ | 帧率 | 30 |
| `resolution` | string | ❌ | 分辨率 | `720p` |
| `user` | string | ❌ | 用户标识符 | - |

### 支持的模型

- `cogvideox`: CogVideoX 主模型
- `cogvideox-text-to-video`: 文生视频
- `cogvideox-image-to-video`: 图生视频

### 支持的分辨率

- `480p` (854x480)
- `720p` (1280x720)
- `1080p` (1920x1080)

### 支持的时长

- `3` 秒
- `5` 秒
- `10` 秒

### 请求示例

#### 文生视频

```json
{
  "model": "cogvideox-text-to-video",
  "prompt": "一只小猫在草地上奔跑,阳光明媚",
  "duration": 5,
  "fps": 30,
  "resolution": "720p"
}
```

#### 图生视频

```json
{
  "model": "cogvideox-image-to-video",
  "prompt": "让图片中的人物动起来,挥手微笑",
  "image_url": "https://example.com/input.jpg",
  "duration": 5,
  "fps": 30,
  "resolution": "720p"
}
```

### 响应格式

```json
{
  "created": 1702839645,
  "data": [
    {
      "url": "https://sfile.chatglm.cn/...",
      "duration": 5,
      "fps": 30,
      "resolution": "720p"
    }
  ]
}
```

## 错误码

| HTTP 状态码 | 错误码 | 说明 |
|-------------|--------|------|
| 400 | invalid_request_error | 参数错误 |
| 401 | authentication_error | API Key 无效 |
| 429 | rate_limit_exceeded | 请求频率超限 |
| 500 | internal_error | 服务端错误 |

## 公共参数抽取

### 图片生成通用参数

- ✅ `prompt` (文本提示词)
- ✅ `model` (模型名称)
- ✅ `size` (尺寸)
- ✅ `quality` (质量等级)
- ✅ `n` (生成数量)
- ❌ `seed` (随机种子 - 不支持)
- ❌ `negative_prompt` (负面提示词 - 不支持)
- ❌ `style` (风格预设 - 不支持)

### 视频生成通用参数

- ✅ `prompt` (文本提示词)
- ✅ `model` (模型名称)
- ✅ `image_url` (参考图片)
- ✅ `duration` (时长)
- ✅ `fps` (帧率)
- ✅ `resolution` (分辨率)
- ❌ `seed` (随机种子 - 不支持)
- ❌ `aspect_ratio` (宽高比 - 使用 resolution 代替)

## 与 OpenAI 的对比

### 相似点

- 端点结构类似 (`/v4/images/generations`)
- 参数名称大部分一致 (`prompt`, `model`, `size`, `quality`, `n`)
- 响应格式相似
- 认证方式相同 (Bearer Token)

### 差异点

| 维度 | OpenAI | 智谱 GLM |
|------|--------|----------|
| **视频生成** | ❌ 不支持 | ✅ 支持 CogVideoX |
| **模型指定** | `dall-e-3` | `cogview-4` |
| **端点路径** | `/v1/images/...` | `/v4/images/...` |
| **域名** | `api.openai.com` | `open.bigmodel.cn` |
| **中文优化** | 一般 | ✅ 强 |
| **图生视频** | ❌ | ✅ |
| **自定义种子** | ❌ | ❌ |

## 特殊能力

- ✅ **中文提示词优化**: CogView-4 对中文提示词有深度优化
- ✅ **视频生成**: 支持文生视频和图生视频
- ✅ **多分辨率**: 视频支持 480p/720p/1080p
- ❌ **无修订提示词**: 不返回修订后的提示词
- ❌ **无风格预设**: 不支持 `vivid`/`natural` 风格

## 定价（参考）

### 图片生成 (CogView-4)

- `1024x1024` standard: ¥0.1 / 图
- `1024x1024` HD: ¥0.2 / 图

### 视频生成 (CogVideoX)

- `480p`, 5 秒: ¥1.0 / 视频
- `720p`, 5 秒: ¥2.0 / 视频
- `1080p`, 5 秒: ¥4.0 / 视频

## 最佳实践

1. **中文提示词**: 使用中文描述能获得更好的效果
2. **视频时长**: 建议从 5 秒开始测试，避免高成本
3. **分辨率选择**: 初期测试使用 480p，确认效果后升级至 720p/1080p
4. **图生视频**: 提供清晰的参考图片能显著提升视频质量
5. **错误重试**: 视频生成可能耗时较长，建议实现超时重试机制
