# xAI Grok Imagine API (图片与视频生成)

## 基本信息

- **厂商**: xAI
- **产品**: Grok Imagine (图片), Grok Imagine Video (视频)
- **端点**: `https://api.x.ai/v1/*`
- **文档**: https://docs.x.ai/
- **图片生成文档**: https://docs.x.ai/developers/model-capabilities/images/generation
- **视频生成文档**: https://docs.x.ai/developers/model-capabilities/video/generation

## 认证

```
Authorization: Bearer YOUR_XAI_API_KEY
```

## 图片生成 API

### 端点

```
POST https://api.x.ai/v1/images/generations
```

### 请求参数

| 参数 | 类型 | 必需 | 说明 | 默认值 |
|------|------|------|------|--------|
| `prompt` | string | ✅ | 文本描述 | - |
| `model` | string | ❌ | 模型名称 | `grok-imagine` |
| `n` | integer | ❌ | 生成数量 (1-5) | 1 |
| `size` | string | ❌ | 图片尺寸 | `1024x1024` |
| `quality` | string | ❌ | 图片质量 | `standard` |
| `aspect_ratio` | string | ❌ | 宽高比 | - |
| `style` | string | ❌ | 风格预设 | - |
| `reference_images` | array | ❌ | 参考图片 URL (最多 5 张) | - |
| `response_format` | string | ❌ | 响应格式 | `url` |

### 支持的模型

- `grok-imagine`: 主图片生成模型

### 支持的尺寸

- `1024x1024`
- `1792x1024` (16:9)
- `1024x1792` (9:16)

### 支持的宽高比

- `1:1` - 正方形
- `16:9` - 横屏
- `9:16` - 竖屏
- `4:3` - 传统横屏
- `3:4` - 传统竖屏

### 支持的质量

- `standard`: 标准质量
- `hd`: 高清质量

### 请求示例

```json
{
  "model": "grok-imagine",
  "prompt": "a futuristic cityscape at night",
  "n": 2,
  "size": "1792x1024",
  "quality": "hd",
  "aspect_ratio": "16:9",
  "response_format": "url"
}
```

#### 使用参考图片

```json
{
  "model": "grok-imagine",
  "prompt": "transform this into a cyberpunk style",
  "reference_images": [
    "https://example.com/ref1.jpg",
    "https://example.com/ref2.jpg"
  ],
  "n": 1,
  "quality": "hd"
}
```

### 响应格式

```json
{
  "created": 1702839645,
  "data": [
    {
      "url": "https://api.x.ai/v1/images/...",
      "revised_prompt": "A high-tech futuristic cityscape..."
    },
    {
      "url": "https://api.x.ai/v1/images/...",
      "revised_prompt": "A high-tech futuristic cityscape..."
    }
  ]
}
```

## 视频生成 API

### 端点

```
POST https://api.x.ai/v1/videos/generations
```

### 请求参数

| 参数 | 类型 | 必需 | 说明 | 默认值 |
|------|------|------|------|--------|
| `prompt` | string | ✅ | 文本描述 | - |
| `model` | string | ❌ | 模型名称 | `grok-imagine-video` |
| `image_url` | string | ❌ | 输入图片 URL (图生视频) | - |
| `duration` | integer | ❌ | 视频时长（秒，3-10） | 5 |
| `aspect_ratio` | string | ❌ | 宽高比 | `16:9` |
| `resolution` | string | ❌ | 分辨率 | `720p` |
| `sound_effects` | boolean | ❌ | 是否生成音效 | false |
| `dialogue` | boolean | ❌ | 是否生成对话 | false |

### 支持的模型

- `grok-imagine-video`: 主视频生成模型

### 支持的分辨率

- `480p` (854x480)
- `720p` (1280x720)
- `1080p` (1920x1080)

### 支持的宽高比

- `16:9` - 横屏
- `9:16` - 竖屏
- `1:1` - 正方形

### 支持的时长

- `3` 秒
- `5` 秒
- `7` 秒
- `10` 秒

### 请求示例

#### 文生视频

```json
{
  "model": "grok-imagine-video",
  "prompt": "a robot walking through a neon-lit street",
  "duration": 5,
  "resolution": "1080p",
  "aspect_ratio": "16:9",
  "sound_effects": true
}
```

#### 图生视频

```json
{
  "model": "grok-imagine-video",
  "prompt": "animate this portrait with a smile",
  "image_url": "https://example.com/portrait.jpg",
  "duration": 5,
  "resolution": "720p",
  "sound_effects": false,
  "dialogue": false
}
```

### 响应格式

视频生成是**异步任务**，需要轮询：

#### 提交响应

```json
{
  "id": "video_abc123",
  "status": "pending",
  "created": 1702839645
}
```

#### 查询状态

```
GET https://api.x.ai/v1/videos/generations/{video_id}
```

#### 完成响应

```json
{
  "id": "video_abc123",
  "status": "completed",
  "created": 1702839645,
  "completed_at": 1702839700,
  "data": {
    "url": "https://api.x.ai/v1/videos/...",
    "duration": 5,
    "resolution": "1080p",
    "aspect_ratio": "16:9",
    "has_sound_effects": true,
    "has_dialogue": false
  }
}
```

#### 失败响应

```json
{
  "id": "video_abc123",
  "status": "failed",
  "error": {
    "code": "generation_failed",
    "message": "Failed to generate video from prompt"
  }
}
```

## SDK 自动轮询

xAI 官方 SDK 会自动处理异步轮询：

```python
# Python SDK 示例
client = xai.Client(api_key="YOUR_API_KEY")

# SDK 内部会自动轮询直到完成
video = client.videos.generate(
    prompt="a robot walking",
    duration=5,
    resolution="1080p"
)

print(video.url)  # 返回时已经是完成状态
```

## 错误码

| HTTP 状态码 | 错误码 | 说明 |
|-------------|--------|------|
| 400 | invalid_request | 参数错误 |
| 401 | authentication_error | API Key 无效 |
| 403 | content_policy_violation | 内容违规 |
| 429 | rate_limit_exceeded | 速率限制 |
| 500 | server_error | 服务端错误 |

## 公共参数抽取

### 图片生成通用参数

- ✅ `prompt` (文本提示词)
- ✅ `model` (模型名称)
- ✅ `n` (生成数量, 1-5)
- ✅ `size` / `aspect_ratio` (尺寸)
- ✅ `quality` (质量等级)
- ✅ `reference_images` (参考图片) ⭐
- ✅ `response_format` (响应格式)
- ❌ `negative_prompt` (不支持)
- ❌ `seed` (不支持)
- ❌ `style` (不支持预设风格)

### 视频生成通用参数

- ✅ `prompt` (文本提示词)
- ✅ `model` (模型名称)
- ✅ `image_url` (输入图片)
- ✅ `duration` (时长)
- ✅ `resolution` (分辨率)
- ✅ `aspect_ratio` (宽高比)
- ✅ `sound_effects` (音效生成) ⭐
- ✅ `dialogue` (对话生成) ⭐
- ❌ `fps` (不支持自定义帧率)
- ❌ `seed` (不支持随机种子)

⭐ 表示其他厂商少见的能力

## 特殊能力

### xAI 独有

- ✅ **同步音效**: 自动生成与视频匹配的音效
- ✅ **对话生成**: 支持为视频生成对话音轨
- ✅ **参考图片编辑**: 图片生成支持最多 5 张参考图
- ✅ **修订提示词**: 返回优化后的提示词
- ✅ **批量图片生成**: 支持 n=1-5
- ❌ **无随机种子**: 无法精确复现

### 与其他厂商对比

| 能力 | xAI Grok | OpenAI | 智谱 GLM | Stability AI |
|------|----------|--------|----------|--------------|
| 图片生成 | ✅ | ✅ | ✅ | ✅ |
| 视频生成 | ✅ | ✅ (Sora) | ✅ | ❌ |
| 音效生成 | ✅ | ❌ | ❌ | ❌ |
| 对话生成 | ✅ | ❌ | ❌ | ❌ |
| 参考图片 | ✅ (5 张) | ❌ | ❌ | ✅ (1 张) |
| 随机种子 | ❌ | ❌ | ❌ | ✅ |
| 负面提示词 | ❌ | ❌ | ❌ | ✅ |

## 定价（参考）

### 图片生成

- `1024x1024` standard: $0.03 / 图
- `1024x1024` HD: $0.06 / 图
- `1792x1024` HD: $0.10 / 图

### 视频生成

按**秒计费**：

- `480p`: $0.10 / 秒
- `720p`: $0.20 / 秒
- `1080p`: $0.40 / 秒
- 音效 / 对话: +$0.05 / 秒

示例：
- 5 秒 1080p 视频 + 音效 = (5 × $0.40) + (5 × $0.05) = $2.25

## 最佳实践

1. **异步处理**: 视频生成使用异步模式，实现轮询或 Webhook
2. **参考图片**: 图片编辑时使用 1-3 张参考图效果最佳
3. **音效选择**: 仅在需要声音的场景启用 `sound_effects`
4. **分辨率选择**: 初期测试使用 720p，确认效果后升级至 1080p
5. **批量生成**: 图片生成可以 `n=2-3` 一次获取多个选项
6. **提示词优化**: Grok 会自动优化提示词，查看 `revised_prompt` 可学习优化方向
7. **错误处理**: 视频生成可能失败，检查 `status` 字段并实现重试机制
