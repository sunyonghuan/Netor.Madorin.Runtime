# Stability AI Stable Diffusion API

## 基本信息

- **厂商**: Stability AI
- **产品**: Stable Diffusion XL, Stable Image Ultra, SD 3.5
- **端点**: `https://api.stability.ai/v2beta/stable-image/generate/*`
- **文档**: https://docs.stability.ai/
- **API 参考**: https://docs.stability.ai/api-reference

## 认证

```
Authorization: Bearer YOUR_API_KEY
```

## 主要端点

### 1. Stable Image Ultra (文生图)

```
POST https://api.stability.ai/v2beta/stable-image/generate/ultra
```

### 2. Stable Diffusion 3.5 (文生图)

```
POST https://api.stability.ai/v2beta/stable-image/generate/sd3
```

### 3. Stable Diffusion XL (文生图)

```
POST https://api.stability.ai/v2beta/stable-image/generate/core
```

### 4. Image-to-Image (图生图)

```
POST https://api.stability.ai/v2beta/stable-image/generate/image-to-image
```

## 核心请求参数

| 参数 | 类型 | 必需 | 说明 | 默认值 |
|------|------|------|------|--------|
| `prompt` | string | ✅ | 文本描述 (最多 10,000 字符) | - |
| `negative_prompt` | string | ❌ | 负面提示词 | - |
| `seed` | integer | ❌ | 随机种子 (0-4294967295) | 随机 |
| `output_format` | string | ❌ | 输出格式 | `png` |
| `aspect_ratio` | string | ❌ | 宽高比 | `1:1` |
| `model` | string | ❌ | 模型版本 | 依端点而定 |

### Stable Image Ultra 特有参数

| 参数 | 类型 | 说明 | 默认值 |
|------|------|------|--------|
| `aspect_ratio` | string | 宽高比 | `1:1` |
| `seed` | integer | 随机种子 | 0 |
| `output_format` | string | `png`, `jpeg`, `webp` | `png` |

### SD 3.5 特有参数

| 参数 | 类型 | 说明 | 默认值 |
|------|------|------|--------|
| `model` | string | `sd3.5-large`, `sd3.5-medium` | `sd3.5-large` |
| `aspect_ratio` | string | 宽高比 | `1:1` |
| `negative_prompt` | string | 负面提示词 | - |
| `seed` | integer | 随机种子 | 0 |
| `output_format` | string | 输出格式 | `png` |

### SDXL (Core) 特有参数

| 参数 | 类型 | 说明 | 默认值 |
|------|------|------|--------|
| `aspect_ratio` | string | 宽高比 | `1:1` |
| `seed` | integer | 随机种子 | 0 |
| `style_preset` | string | 风格预设 | - |
| `output_format` | string | 输出格式 | `png` |

### Image-to-Image 特有参数

| 参数 | 类型 | 必需 | 说明 |
|------|------|------|------|
| `image` | file | ✅ | 输入图片 (PNG/JPEG/WEBP) |
| `prompt` | string | ✅ | 文本描述 |
| `strength` | float | ❌ | 变换强度 (0.0-1.0) |
| `negative_prompt` | string | ❌ | 负面提示词 |
| `seed` | integer | ❌ | 随机种子 |
| `output_format` | string | ❌ | 输出格式 |

## 宽高比选项

| 宽高比 | 尺寸 | 说明 |
|--------|------|------|
| `1:1` | 1024x1024 | 正方形 |
| `16:9` | 1344x768 | 横屏 |
| `9:16` | 768x1344 | 竖屏 |
| `21:9` | 1536x640 | 超宽屏 |
| `9:21` | 640x1536 | 超窄屏 |
| `4:3` | 1152x896 | 传统横屏 |
| `3:4` | 896x1152 | 传统竖屏 |
| `3:2` | 1216x832 | 照片横向 |
| `2:3` | 832x1216 | 照片竖向 |

## 风格预设 (SDXL)

- `3d-model`
- `analog-film`
- `anime`
- `cinematic`
- `comic-book`
- `digital-art`
- `enhance`
- `fantasy-art`
- `isometric`
- `line-art`
- `low-poly`
- `modeling-compound`
- `neon-punk`
- `origami`
- `photographic`
- `pixel-art`
- `tile-texture`

## 请求示例

### Stable Image Ultra

```bash
curl -f https://api.stability.ai/v2beta/stable-image/generate/ultra \
  -H "authorization: Bearer YOUR_API_KEY" \
  -H "accept: image/*" \
  -F "prompt=a beautiful mountain landscape" \
  -F "aspect_ratio=16:9" \
  -F "output_format=webp" \
  -F "seed=42" \
  -o result.webp
```

### SD 3.5 JSON 请求

```json
{
  "prompt": "a serene japanese garden with koi pond",
  "negative_prompt": "blurry, low quality",
  "model": "sd3.5-large",
  "aspect_ratio": "16:9",
  "seed": 12345,
  "output_format": "png"
}
```

## 响应格式

**成功响应** (直接返回图片二进制):

```
Content-Type: image/png
[Binary Image Data]
```

**错误响应** (JSON):

```json
{
  "id": "error_id",
  "name": "bad_request",
  "errors": ["Prompt must not be empty"]
}
```

## 错误码

| HTTP 状态码 | 错误名称 | 说明 |
|-------------|----------|------|
| 400 | bad_request | 参数错误 |
| 401 | unauthorized | API Key 无效 |
| 402 | payment_required | 余额不足 |
| 403 | forbidden | 内容违规 |
| 404 | not_found | 端点不存在 |
| 413 | payload_too_large | 输入图片过大 |
| 429 | rate_limit_exceeded | 速率限制 |
| 500 | server_error | 服务端错误 |

## 公共参数抽取

### 图片生成通用参数

- ✅ `prompt` (文本提示词)
- ✅ `negative_prompt` (负面提示词) ⭐
- ✅ `seed` (随机种子) ⭐
- ✅ `aspect_ratio` (宽高比) ⭐
- ✅ `output_format` (输出格式)
- ✅ `style_preset` (风格预设) ⭐
- ✅ `strength` (图生图强度) ⭐
- ❌ `quality` (无质量参数)
- ❌ `n` (不支持批量生成)

⭐ 表示 OpenAI DALL-E 不支持的能力

## 特殊能力

### Stability AI 独有

- ✅ **负面提示词**: 精确控制不想要的元素
- ✅ **随机种子**: 可复现生成结果
- ✅ **宽高比控制**: 更灵活的尺寸选择
- ✅ **风格预设**: SDXL 支持 17+ 内置风格
- ✅ **图生图**: 基于输入图片进行变换
- ✅ **多模型**: Ultra, 3.5, XL 不同质量等级
- ❌ **无批量生成**: 不支持 `n` 参数

### 与 OpenAI 对比

| 能力 | Stability AI | OpenAI DALL-E |
|------|--------------|---------------|
| 负面提示词 | ✅ | ❌ |
| 随机种子 | ✅ | ❌ |
| 风格预设 | ✅ (17+ 种) | ✅ (仅 vivid/natural) |
| 图生图 | ✅ | ❌ |
| 宽高比灵活性 | ✅ (9 种) | ❌ (3 种固定尺寸) |
| 批量生成 | ❌ | ✅ (DALL-E 2) |
| 中文支持 | 一般 | 良好 |

## 定价（参考）

| 模型 | 价格 |
|------|------|
| Stable Image Ultra | $0.08 / 图 |
| SD 3.5 Large | $0.065 / 图 |
| SD 3.5 Medium | $0.040 / 图 |
| SDXL (Core) | $0.030 / 图 |
| Image-to-Image | $0.030 / 图 |

## 最佳实践

1. **使用负面提示词**: 排除不想要的元素（如 "blurry, low quality"）
2. **固定种子**: 需要复现时使用相同的 seed
3. **选择合适模型**: Ultra 适合照片级, 3.5 适合艺术创作, SDXL 适合快速迭代
4. **输出格式**: WebP 可减少传输大小，PNG 保留最高质量
5. **宽高比**: 根据使用场景选择（社交媒体 1:1, 海报 9:16, 横幅 21:9）
6. **风格预设**: SDXL 使用风格预设比在 prompt 中描述风格更稳定
