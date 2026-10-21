# Replicate API (图片与视频生成聚合平台)

## 基本信息

- **厂商**: Replicate
- **产品**: 托管的开源模型平台
- **基础域名**: `https://api.replicate.com`
- **文档**: https://replicate.com/docs
- **模型库**: https://replicate.com/explore

## 认证

```
Authorization: Bearer YOUR_REPLICATE_API_TOKEN
```

## 统一 API 结构

Replicate 托管数百个开源模型，所有模型使用相同的 API 结构：

```
POST https://api.replicate.com/v1/predictions
```

## 核心概念

- **Model**: 模型标识，格式为 `owner/model-name:version`
- **Prediction**: 单次推理任务
- **Input**: 模型输入参数（每个模型不同）
- **Output**: 生成结果 URL

## 通用请求参数

| 参数 | 类型 | 必需 | 说明 |
|------|------|------|------|
| `version` | string | ✅ | 模型版本 ID (以 `v_` 开头的 64 字符) |
| `input` | object | ✅ | 模型输入参数（模型特定） |
| `webhook` | string | ❌ | Webhook URL（任务完成时回调） |
| `webhook_events_filter` | array | ❌ | Webhook 事件过滤 |

## 图片生成热门模型

### 1. FLUX.1 [dev] (Black Forest Labs)

**模型 ID**: `black-forest-labs/flux-dev`

**输入参数**:

| 参数 | 类型 | 说明 | 默认值 |
|------|------|------|--------|
| `prompt` | string | 文本描述 | - |
| `width` | integer | 宽度 (128-1440) | 1024 |
| `height` | integer | 高度 (128-1440) | 1024 |
| `num_outputs` | integer | 生成数量 (1-4) | 1 |
| `num_inference_steps` | integer | 推理步数 (1-50) | 28 |
| `guidance_scale` | float | 引导强度 (0-10) | 3.5 |
| `seed` | integer | 随机种子 | 随机 |

### 2. Stable Diffusion XL

**模型 ID**: `stability-ai/sdxl:latest`

**输入参数**:

| 参数 | 类型 | 说明 | 默认值 |
|------|------|------|--------|
| `prompt` | string | 文本描述 | - |
| `negative_prompt` | string | 负面提示词 | - |
| `width` | integer | 宽度 (128-1024) | 1024 |
| `height` | integer | 高度 (128-1024) | 1024 |
| `num_outputs` | integer | 生成数量 | 1 |
| `num_inference_steps` | integer | 推理步数 | 50 |
| `guidance_scale` | float | 引导强度 | 7.5 |
| `seed` | integer | 随机种子 | 随机 |
| `refine` | string | 精炼模式 | `no_refiner` |

### 3. Playground v2.5

**模型 ID**: `playgroundai/playground-v2.5-1024px-aesthetic`

**输入参数**: 与 SDXL 类似

## 视频生成热门模型

### 1. SeedANCE 2.5 (ByteDance)

**模型 ID**: `bytedance/seedance-25`

**输入参数**:

| 参数 | 类型 | 说明 | 默认值 |
|------|------|------|--------|
| `prompt` | string | 文本描述 | - |
| `image` | string | 输入图片 URL（图生视频） | - |
| `duration` | integer | 时长（秒，3-10） | 5 |
| `resolution` | string | 分辨率 | `720p` |
| `fps` | integer | 帧率 | 30 |

### 2. Kling v2.1

**模型 ID**: `kwai/kling-v2-1`

**输入参数**:

| 参数 | 类型 | 说明 | 默认值 |
|------|------|------|--------|
| `prompt` | string | 文本描述 | - |
| `image` | string | 输入图片 URL | - |
| `duration` | string | `5s` 或 `10s` | `5s` |
| `resolution` | string | `720p` 或 `1080p` | `720p` |
| `aspect_ratio` | string | `16:9`, `9:16`, `1:1` | `16:9` |

### 3. Stable Video Diffusion

**模型 ID**: `stability-ai/stable-video-diffusion-img2vid-xt`

**输入参数**:

| 参数 | 类型 | 说明 |
|------|------|------|
| `image` | string | 输入图片 URL（必需） |
| `motion_bucket_id` | integer | 运动强度 (1-255) |
| `cond_aug` | float | 条件增强 (0-1) |
| `fps` | integer | 帧率 (6-30) |

## 请求示例

### 图片生成 (FLUX.1)

```json
{
  "version": "v_abc123...",
  "input": {
    "prompt": "a serene mountain landscape",
    "width": 1024,
    "height": 1024,
    "num_outputs": 1,
    "guidance_scale": 3.5,
    "seed": 42
  }
}
```

### 视频生成 (SeedANCE)

```json
{
  "version": "v_xyz789...",
  "input": {
    "prompt": "a cat walking on grass",
    "duration": 5,
    "resolution": "720p",
    "fps": 30
  }
}
```

## 响应格式

### 提交响应 (异步)

```json
{
  "id": "pred_abc123",
  "version": "v_...",
  "status": "starting",
  "input": { ... },
  "output": null,
  "created_at": "2026-10-20T03:00:00Z",
  "started_at": null,
  "completed_at": null,
  "urls": {
    "get": "https://api.replicate.com/v1/predictions/pred_abc123",
    "cancel": "https://api.replicate.com/v1/predictions/pred_abc123/cancel"
  }
}
```

### 轮询响应 (进行中)

```json
{
  "id": "pred_abc123",
  "status": "processing",
  "progress": 0.45,
  "logs": "Step 23/50...",
  ...
}
```

### 完成响应

```json
{
  "id": "pred_abc123",
  "status": "succeeded",
  "output": [
    "https://replicate.delivery/pbxt/abc123.png"
  ],
  "metrics": {
    "predict_time": 3.45
  },
  "completed_at": "2026-10-20T03:00:15Z"
}
```

### 失败响应

```json
{
  "id": "pred_abc123",
  "status": "failed",
  "error": "NSFW content detected",
  "completed_at": "2026-10-20T03:00:10Z"
}
```

## 状态枚举

| 状态 | 说明 |
|------|------|
| `starting` | 正在启动 |
| `processing` | 生成中 |
| `succeeded` | 成功 |
| `failed` | 失败 |
| `canceled` | 已取消 |

## 查询任务状态

```
GET https://api.replicate.com/v1/predictions/{prediction_id}
```

## 取消任务

```
POST https://api.replicate.com/v1/predictions/{prediction_id}/cancel
```

## Webhook 模式

### 设置 Webhook

```json
{
  "version": "v_...",
  "input": { ... },
  "webhook": "https://your-domain.com/webhook",
  "webhook_events_filter": ["completed"]
}
```

### Webhook 回调

```json
{
  "id": "pred_abc123",
  "status": "succeeded",
  "output": ["https://..."],
  "completed_at": "..."
}
```

## 错误码

| HTTP 状态码 | 说明 |
|-------------|------|
| 400 | 参数错误 |
| 401 | Token 无效 |
| 402 | 余额不足 |
| 404 | Prediction 不存在 |
| 429 | 速率限制 |
| 500 | 服务端错误 |

## 公共参数抽取

### 图片生成通用参数

- ✅ `prompt` (文本提示词)
- ✅ `negative_prompt` (负面提示词，部分模型)
- ✅ `width` / `height` (尺寸)
- ✅ `num_outputs` (生成数量)
- ✅ `num_inference_steps` (推理步数) ⭐
- ✅ `guidance_scale` (引导强度) ⭐
- ✅ `seed` (随机种子) ⭐
- ❌ `quality` (无统一质量参数)
- ❌ `style` (无统一风格预设)

### 视频生成通用参数

- ✅ `prompt` (文本提示词)
- ✅ `image` (输入图片)
- ✅ `duration` (时长)
- ✅ `resolution` (分辨率)
- ✅ `fps` (帧率)
- ✅ `aspect_ratio` (宽高比)
- ✅ `motion_bucket_id` (运动强度，特定模型) ⭐
- ❌ `seed` (多数视频模型不支持)

⭐ 表示 OpenAI 不支持的能力

## 特殊能力

### Replicate 平台特性

- ✅ **统一 API**: 所有模型使用相同的 API 结构
- ✅ **版本锁定**: 通过 `version` 锁定模型版本
- ✅ **异步任务**: 所有任务都是异步的
- ✅ **Webhook 支持**: 任务完成自动回调
- ✅ **进度查询**: `progress` 字段显示进度
- ✅ **日志流**: `logs` 字段实时日志
- ✅ **模型多样性**: 数百个开源模型可选
- ✅ **取消任务**: 支持取消进行中的任务
- ❌ **参数差异**: 每个模型的 `input` 参数不同
- ❌ **文档碎片化**: 需查看每个模型的文档

### 与其他厂商对比

| 能力 | Replicate | OpenAI | Stability AI |
|------|-----------|--------|--------------|
| 统一 API | ✅ | ✅ | ❌ (多端点) |
| 异步任务 | ✅ (所有) | ❌ | ❌ (图片同步) |
| Webhook | ✅ | ❌ | ❌ |
| 进度查询 | ✅ | ❌ | ❌ |
| 模型选择 | ✅ (100+) | ❌ (固定) | ✅ (少量) |
| 参数一致性 | ❌ | ✅ | ✅ |
| 取消任务 | ✅ | ❌ | ❌ |

## 定价

Replicate 按计算时间计费，不同模型价格不同：

| 模型 | 价格 |
|------|------|
| FLUX.1 [dev] | ~$0.003 / 秒 |
| SDXL | ~$0.002 / 秒 |
| Kling v2.1 | ~$0.04 / 秒 |
| SeedANCE 2.5 | ~$0.03 / 秒 |

*价格示例，实际价格见模型页面*

## 最佳实践

1. **版本锁定**: 始终使用 `version` 而非 `latest` 标签
2. **Webhook 模式**: 长时间任务使用 Webhook 而非轮询
3. **错误处理**: 检查 `status` 字段判断成功/失败
4. **取消机制**: 实现用户取消时调用 `/cancel` 端点
5. **日志监控**: 使用 `logs` 字段诊断问题
6. **模型选择**: 根据质量、速度、成本平衡选择模型
7. **参数优化**: 查阅具体模型文档了解最佳参数
8. **并发控制**: Replicate 限制并发任务数，实现队列机制

## Python SDK 示例

```python
import replicate

# 图片生成
output = replicate.run(
    "black-forest-labs/flux-dev",
    input={
        "prompt": "a serene landscape",
        "width": 1024,
        "height": 1024,
        "seed": 42
    }
)
print(output[0])  # 图片 URL

# 视频生成
output = replicate.run(
    "bytedance/seedance-25",
    input={
        "prompt": "a cat walking",
        "duration": 5,
        "resolution": "720p"
    }
)
print(output)  # 视频 URL
```

## 热门模型对比

| 模型 | 类型 | 速度 | 质量 | 价格 | 推荐场景 |
|------|------|------|------|------|----------|
| FLUX.1 [dev] | 图片 | 快 | 高 | 中 | 通用图片生成 |
| SDXL | 图片 | 中 | 中 | 低 | 成本敏感 |
| Playground v2.5 | 图片 | 快 | 高 | 中 | 艺术创作 |
| Kling v2.1 | 视频 | 慢 | 高 | 高 | 高质量视频 |
| SeedANCE 2.5 | 视频 | 中 | 中 | 中 | 平衡方案 |
| SVD | 视频 | 快 | 中 | 低 | 图生视频 |
