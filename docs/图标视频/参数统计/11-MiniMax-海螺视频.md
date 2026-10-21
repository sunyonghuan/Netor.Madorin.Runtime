# MiniMax API (图像生成 + 海螺视频)

## 基本信息

- **厂商**: MiniMax (海螺 AI)
- **产品**: 图像生成 + 海螺视频 (Hailuo Video)
- **官网**: https://www.minimaxi.com
- **开放平台**: https://platform.minimaxi.com
- **CLI**: ❌ 仅 HTTP API
- **文档**: 平台文档中心

## 统一 API 特点

MiniMax 提供统一 API,单个密钥访问文本、图像、视频、音频生成能力。

**定价**: ~$0.3 / 百万 tokens (统一计费)

## 视频生成 API (海螺视频 - MiniMax H3)

### 模型版本

| 模型 | 说明 | 参数量 |
|------|------|--------|
| `minimax-h3` | MiniMax H3 主模型 | 大规模 MoE |
| `hailuo-video` | 海螺视频模型 | - |

### 核心请求参数

| 参数 | 类型 | 必需 | 说明 | 默认值 |
|------|------|------|------|--------|
| `prompt` | string | ✅ | 文本描述 | - |
| `model` | string | ❌ | 模型名称 | `minimax-h3` |
| `mode` | string | ❌ | 生成模式 | `text-to-video` |
| `image_url` | string | ❌ | 输入图片 URL (图生视频) | - |
| `first_frame_url` | string | ❌ | 首帧图片 URL | - |
| `last_frame_url` | string | ❌ | 尾帧图片 URL | - |
| `duration` | integer | ❌ | 视频时长（秒） | 5 |
| `resolution` | string | ❌ | 分辨率 | `720p` |
| `camera_control` | object | ❌ | 摄像机控制命令 | - |
| `keyframe_editing` | boolean | ❌ | 关键帧编辑 | false |
| `native_stereo` | boolean | ❌ | 原生立体声 | false |

### 生成模式

- `text-to-video`: 文生视频
- `image-to-video`: 图生视频
- `first-frame`: 首帧生成视频
- `first-last-frame`: 首尾帧生成视频
- `multi-shot`: 多镜头序列
- `reference-video`: 参考视频生成

### 支持的分辨率

- `720p` (1280x720)
- `1080p` (1920x1080)
- `2K` (2560x1440)

### 摄像机控制命令

```json
{
  "camera_control": {
    "command": "zoom_in",       // 控制命令
    "intensity": "medium",      // 强度
    "timing": "start"           // 时机
  }
}
```

**支持的命令**:
- `zoom_in` / `zoom_out` - 推进/拉远
- `pan_left` / `pan_right` - 左右摇
- `tilt_up` / `tilt_down` - 上下仰俯
- `dolly_forward` / `dolly_backward` - 移动
- `orbit` - 环绕

### 关键帧编辑

```json
{
  "keyframe_editing": true,
  "keyframes": [
    {
      "time": 0.0,
      "description": "场景开始"
    },
    {
      "time": 2.5,
      "description": "镜头切换到特写"
    },
    {
      "time": 5.0,
      "description": "场景结束"
    }
  ]
}
```

### 请求示例

#### 文生视频

```json
{
  "model": "minimax-h3",
  "mode": "text-to-video",
  "prompt": "一只小猫在花园里玩耍，镜头从远处推进",
  "duration": 5,
  "resolution": "1080p",
  "camera_control": {
    "command": "zoom_in",
    "intensity": "medium"
  },
  "native_stereo": true
}
```

#### 首尾帧生成视频

```json
{
  "model": "minimax-h3",
  "mode": "first-last-frame",
  "prompt": "平滑过渡",
  "first_frame_url": "https://example.com/first.jpg",
  "last_frame_url": "https://example.com/last.jpg",
  "duration": 5,
  "resolution": "2K"
}
```

#### 多镜头序列

```json
{
  "model": "minimax-h3",
  "mode": "multi-shot",
  "prompt": "产品展示视频，多角度拍摄",
  "duration": 10,
  "resolution": "1080p",
  "keyframe_editing": true,
  "keyframes": [
    {"time": 0.0, "description": "正面视角"},
    {"time": 3.0, "description": "侧面视角"},
    {"time": 6.0, "description": "顶部俯视"},
    {"time": 10.0, "description": "45度特写"}
  ]
}
```

### 响应格式

**提交响应** (异步):

```json
{
  "job_id": "minimax_abc123",
  "status": "pending",
  "estimated_time": 120
}
```

**完成响应**:

```json
{
  "job_id": "minimax_abc123",
  "status": "completed",
  "result": {
    "video_url": "https://...",
    "duration": 5,
    "resolution": "1080p",
    "has_stereo_audio": true,
    "file_size_mb": 15.6
  }
}
```

## 图像生成 API

### 核心请求参数

| 参数 | 类型 | 必需 | 说明 | 默认值 |
|------|------|------|------|--------|
| `prompt` | string | ✅ | 文本描述 | - |
| `negative_prompt` | string | ❌ | 负面提示词 | - |
| `width` | integer | ❌ | 宽度 (256-2048) | 1024 |
| `height` | integer | ❌ | 高度 (256-2048) | 1024 |
| `num_images` | integer | ❌ | 生成数量 (1-4) | 1 |
| `style` | string | ❌ | 风格预设 | - |
| `seed` | integer | ❌ | 随机种子 | 随机 |
| `quality` | string | ❌ | 质量等级 | `standard` |

### 支持的风格

- `realistic` - 写实
- `anime` - 动漫
- `cartoon` - 卡通
- `sketch` - 素描
- `watercolor` - 水彩
- `oil_painting` - 油画
- `chinese_painting` - 国画

### 质量等级

- `standard`: 标准质量
- `high`: 高质量
- `ultra`: 超高质量

### 请求示例

```json
{
  "prompt": "一幅山水画，水墨风格，意境深远",
  "negative_prompt": "模糊，低质量，噪点",
  "width": 1024,
  "height": 1024,
  "num_images": 2,
  "style": "chinese_painting",
  "quality": "high",
  "seed": 42
}
```

### 响应格式

```json
{
  "images": [
    {
      "url": "https://...",
      "seed": 42,
      "width": 1024,
      "height": 1024
    },
    {
      "url": "https://...",
      "seed": 43,
      "width": 1024,
      "height": 1024
    }
  ]
}
```

## 认证方式

```
Authorization: Bearer YOUR_MINIMAX_API_KEY
```

## 错误码

| HTTP 状态码 | 错误码 | 说明 |
|-------------|--------|------|
| 400 | invalid_request | 参数错误 |
| 401 | authentication_failed | 认证失败 |
| 402 | insufficient_credits | 积分不足 |
| 429 | rate_limit_exceeded | 速率限制 |
| 500 | server_error | 服务端错误 |

## 公共参数抽取

### 视频生成通用参数

- ✅ `prompt` (文本提示词)
- ✅ `model` (模型名称)
- ✅ `image_url` (图生视频输入)
- ✅ `first_frame_url` (首帧) ⭐
- ✅ `last_frame_url` (尾帧) ⭐
- ✅ `duration` (时长)
- ✅ `resolution` (分辨率)
- ✅ `camera_control` (摄像机控制) ⭐
- ✅ `keyframe_editing` (关键帧编辑) ⭐
- ✅ `native_stereo` (立体声) ⭐
- ✅ `multi_shot` (多镜头) ⭐
- ❌ `fps` (不支持自定义)
- ❌ `aspect_ratio` (使用 resolution)

### 图片生成通用参数

- ✅ `prompt` (文本提示词)
- ✅ `negative_prompt` (负面提示词) ⭐
- ✅ `width` / `height` (尺寸，灵活)
- ✅ `num_images` (生成数量)
- ✅ `style` (风格预设)
- ✅ `seed` (随机种子) ⭐
- ✅ `quality` (质量等级)
- ❌ `guidance_scale` (不支持)
- ❌ `steps` (不支持)

⭐ 表示独特或高级能力

## 特殊能力

### MiniMax H3 视频独有

- ✅ **首尾帧生成**: 指定首尾帧的视频生成
- ✅ **多镜头序列**: 自动多镜头切换
- ✅ **原生立体声**: 自动生成立体声音轨
- ✅ **精确摄像机控制**: 多种摄像机运动命令
- ✅ **关键帧编辑**: 时间轴上精确控制
- ✅ **2K 输出**: 支持 2K 高清输出
- ✅ **参考视频**: 基于参考视频生成新视频
- ✅ **中文优化**: 对中文提示词深度优化

### 图像生成

- ✅ **灵活尺寸**: 256-2048 任意尺寸
- ✅ **负面提示词**: 精确控制
- ✅ **随机种子**: 可复现
- ✅ **风格丰富**: 7+ 内置风格
- ✅ **中文优化**: 强

## 第三方接入

MiniMax H3 API 可通过以下平台接入：

- **腾讯云 TokenHub**: 海螺视频文档与接口
- **Pollo.ai**: api.pollo.ai 提供 MiniMax H3 API
- **ComfyUI**: MinimaxTextToVideoNode 节点支持
- **GitHub**: 开源项目集成示例

## 定价（参考）

### 统一计费模式

- 按 tokens 计费: ~$0.3 / 百万 tokens
- 图片生成: ~1,000 tokens / 张
- 视频生成: 根据时长和分辨率计算 tokens

### 估算

| 类型 | 规格 | 价格 |
|------|------|------|
| 图片 | 1024x1024 | ~¥0.002 |
| 视频 | 720p, 5s | ~¥0.5 |
| 视频 | 1080p, 5s | ~¥1.0 |
| 视频 | 2K, 5s | ~¥2.0 |
| 视频 | 1080p, 10s | ~¥2.0 |

*具体价格以平台实际计费为准*

## 最佳实践

### 视频生成

1. **首尾帧模式**: 需要精确控制时使用首尾帧
2. **关键帧编辑**: 复杂动画使用关键帧精确控制
3. **摄像机控制**: 使用命令而非在 prompt 中描述镜头运动
4. **多镜头**: 产品展示、教学视频使用多镜头模式
5. **立体声**: 需要音效时启用 `native_stereo`
6. **中文提示词**: 使用中文描述效果最佳
7. **分辨率选择**: 测试用 720p，正式用 1080p，展示用 2K

### 图像生成

1. **负面提示词**: 添加"模糊,低质量,噪点"等
2. **风格选择**: 明确使用场景选择合适风格
3. **固定种子**: 使用相同 seed 复现结果
4. **质量等级**: 重要场景使用 `ultra`

## 与其他厂商对比

| 能力 | MiniMax | 快手可灵 | 智谱 | xAI |
|------|---------|---------|------|-----|
| 文生视频 | ✅ | ✅ | ✅ | ✅ |
| 图生视频 | ✅ | ✅ | ✅ | ✅ |
| 首尾帧生成 | ✅ | ✅ | ❌ | ❌ |
| 多镜头 | ✅ | ✅ | ❌ | ❌ |
| 关键帧编辑 | ✅ | ❌ | ❌ | ❌ |
| 摄像机控制 | ✅ | ✅ | ❌ | ❌ |
| 立体声 | ✅ | ❌ | ❌ | ✅ |
| 2K 输出 | ✅ | ❌ | ❌ | ❌ |
| 图片生成 | ✅ | ✅ | ✅ | ✅ |
| 中文优化 | ✅ 强 | ✅ 强 | ✅ 强 | 一般 |
| CLI 支持 | ❌ | ❌ | ❌ | ❌ |

## 接入建议

- **优先级**: 🔥 高优先级 (图片+视频双支持，关键帧编辑独特)
- **接入方式**: 自建技能 CLI
- **适用场景**: 产品展示、教学视频、动画制作、图片生成
- **优势**: 首尾帧、关键帧编辑、2K 输出、立体声、统一 API
- **统一计费**: 单个 API Key 访问所有模态，计费简单
