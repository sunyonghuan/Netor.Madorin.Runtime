# 生数科技 Vidu API (视频生成)

## 基本信息

- **厂商**: 生数科技 (ShengShu Technology)
- **产品**: Vidu (视频生成)
- **官网**: https://www.vidu.cn
- **开放平台**: https://platform.vidu.cn
- **CLI**: ❌ 仅 HTTP API (社区有 vidu-cli)
- **文档**: https://platform.vidu.cn/docs

## 产品定位

Vidu 专注于**视频生成**,不提供图片生成能力。

## 模型版本

| 模型 | 说明 | 特点 |
|------|------|------|
| `viduq3-turbo` | Q3 极速版 | 最快生成，最佳性价比，智能切镜，音画同出 |
| `viduq3` | Q3 标准版 | 增强多摄像机一致性，智能切换 |
| `viduq2-pro` | Q2 专业版 | 支持参考视频，视频编辑，视频替换 |
| `viduq2` | Q2 标准版 | 动态效果好，细节丰富 |
| `viduq1` | Q1 标准版 | 画面清晰，转场流畅，镜头稳定 |
| `vidu2` | V2 版本 | 早期版本 |

## 核心请求参数

| 参数 | 类型 | 必需 | 说明 | 默认值 |
|------|------|------|------|--------|
| `prompt` | string | ✅ | 文本描述（最多 ~1500 字符） | - |
| `model` | string | ❌ | 模型版本 | `viduq3-turbo` |
| `mode` | string | ❌ | 生成模式 | `text-to-video` |
| `image_url` | string | ❌ | 输入图片 URL (图生视频) | - |
| `first_frame_url` | string | ❌ | 首帧图片 URL | - |
| `last_frame_url` | string | ❌ | 尾帧图片 URL | - |
| `reference_images` | array | ❌ | 多图参考 | - |
| `duration` | integer | ❌ | 视频时长（秒，4/8/16） | 4 |
| `resolution` | string | ❌ | 分辨率 | `720p` |
| `aspect_ratio` | string | ❌ | 宽高比 | `16:9` |
| `camera_switching` | boolean | ❌ | 智能切镜 (Q3) | false |
| `audio_video_sync` | boolean | ❌ | 音画同出 (Q3) | false |

## 生成模式

- `text-to-video`: 文生视频
- `image-to-video`: 图生视频
- `first-frame-video`: 首帧生成视频
- `first-last-frame-video`: 首尾帧生成视频
- `multi-image-reference`: 多图参考生成
- `reference-video`: 参考视频生成 (Q2-pro)
- `video-editing`: 视频编辑 (Q2-pro)
- `video-replacement`: 视频替换 (Q2-pro)

## 支持的时长

- `4` 秒
- `8` 秒
- `16` 秒

## 支持的分辨率

- `480p` (854x480)
- `720p` (1280x720)
- `1080p` (1920x1080)

## 支持的宽高比

- `16:9` - 横屏
- `9:16` - 竖屏
- `1:1` - 正方形
- `4:3` - 传统横屏
- `3:4` - 传统竖屏

## 请求示例

### 文生视频

```json
{
  "model": "viduq3-turbo",
  "mode": "text-to-video",
  "prompt": "一只小猫在花园里追逐蝴蝶，阳光洒在草地上",
  "duration": 8,
  "resolution": "720p",
  "aspect_ratio": "16:9",
  "camera_switching": true,
  "audio_video_sync": true
}
```

### 图生视频

```json
{
  "model": "viduq3",
  "mode": "image-to-video",
  "prompt": "让画面动起来，人物微笑挥手",
  "image_url": "https://example.com/input.jpg",
  "duration": 4,
  "resolution": "1080p"
}
```

### 首尾帧生成视频

```json
{
  "model": "viduq2",
  "mode": "first-last-frame-video",
  "prompt": "平滑过渡，自然衔接",
  "first_frame_url": "https://example.com/first.jpg",
  "last_frame_url": "https://example.com/last.jpg",
  "duration": 8,
  "resolution": "720p"
}
```

### 多图参考生成

```json
{
  "model": "viduq3",
  "mode": "multi-image-reference",
  "prompt": "融合这些图片的风格和元素",
  "reference_images": [
    {"url": "https://example.com/ref1.jpg", "weight": 1.0},
    {"url": "https://example.com/ref2.jpg", "weight": 0.8},
    {"url": "https://example.com/ref3.jpg", "weight": 0.6}
  ],
  "duration": 8,
  "resolution": "720p"
}
```

### 参考视频生成 (Q2-pro)

```json
{
  "model": "viduq2-pro",
  "mode": "reference-video",
  "prompt": "模仿这个视频的风格",
  "reference_video_url": "https://example.com/reference.mp4",
  "duration": 8,
  "resolution": "720p"
}
```

## 响应格式

**提交响应** (异步):

```json
{
  "task_id": "vidu_abc123",
  "status": "pending",
  "estimated_time": 180,
  "created_at": "2026-10-20T04:00:00Z"
}
```

**查询状态**:

```
GET /api/v1/tasks/{task_id}
```

**完成响应**:

```json
{
  "task_id": "vidu_abc123",
  "status": "completed",
  "result": {
    "video_url": "https://...",
    "duration": 8,
    "resolution": "720p",
    "aspect_ratio": "16:9",
    "file_size_mb": 12.5,
    "has_audio": true
  },
  "completed_at": "2026-10-20T04:03:00Z"
}
```

## 认证方式

```
Authorization: Bearer YOUR_VIDU_API_KEY
```

## 错误码

| HTTP 状态码 | 错误码 | 说明 |
|-------------|--------|------|
| 400 | invalid_parameter | 参数错误 |
| 401 | unauthorized | API Key 无效 |
| 402 | insufficient_balance | 余额不足 |
| 413 | prompt_too_long | 提示词过长 (>1500 字符) |
| 429 | rate_limit_exceeded | 速率限制 |
| 500 | internal_error | 服务端错误 |

## 公共参数抽取

### 视频生成通用参数

- ✅ `prompt` (文本提示词，最多 ~1500 字符)
- ✅ `model` (模型版本)
- ✅ `image_url` (图生视频输入)
- ✅ `first_frame_url` (首帧) ⭐
- ✅ `last_frame_url` (尾帧) ⭐
- ✅ `reference_images` (多图参考) ⭐
- ✅ `reference_video_url` (参考视频) ⭐
- ✅ `duration` (时长: 4/8/16s)
- ✅ `resolution` (分辨率)
- ✅ `aspect_ratio` (宽高比)
- ✅ `camera_switching` (智能切镜，Q3) ⭐
- ✅ `audio_video_sync` (音画同出，Q3) ⭐
- ❌ `fps` (不支持自定义)
- ❌ `seed` (不支持随机种子)
- ❌ `camera_control` (不支持精确控制)

⭐ 表示独特或高级能力

## 特殊能力

### Vidu 独有

- ✅ **智能切镜**: viduq3 系列自动智能切换镜头
- ✅ **音画同出**: viduq3-turbo 自动生成匹配音效
- ✅ **多摄像机一致性**: viduq3 增强多镜头一致性
- ✅ **首尾帧生成**: 指定首尾帧的视频生成
- ✅ **多图参考**: 支持多张图片参考
- ✅ **参考视频**: Q2-pro 支持参考视频生成
- ✅ **视频编辑**: Q2-pro 支持视频编辑
- ✅ **视频替换**: Q2-pro 支持视频替换
- ✅ **长提示词**: 支持最多 ~1500 字符
- ✅ **中文优化**: 对中文提示词优化

### 模型特色对比

| 能力 | Q3-turbo | Q3 | Q2-pro | Q2 | Q1 |
|------|----------|----|----|----|----|
| 速度 | ⚡ 最快 | 快 | 中 | 中 | 慢 |
| 质量 | 高 | 高 | 最高 | 高 | 中 |
| 智能切镜 | ✅ | ✅ | ❌ | ❌ | ❌ |
| 音画同出 | ✅ | ❌ | ❌ | ❌ | ❌ |
| 参考视频 | ❌ | ❌ | ✅ | ❌ | ❌ |
| 视频编辑 | ❌ | ❌ | ✅ | ❌ | ❌ |
| 价格 | 低 | 中 | 高 | 中 | 低 |

## 第三方接入

Vidu API 可通过以下平台接入：

- **腾讯云**: 通过腾讯云接入
- **阿里云百炼**: 通过阿里云百炼平台接入
- **ComfyUI**: comfyui_vidu_api 节点支持
- **社区 CLI**: vidu-cli 命令行工具 (非官方)

## 定价（参考）

### 按模型计费

| 模型 | 4s | 8s | 16s |
|------|----|----|-----|
| viduq3-turbo | ¥0.3 | ¥0.6 | ¥1.2 |
| viduq3 | ¥0.5 | ¥1.0 | ¥2.0 |
| viduq2-pro | ¥1.0 | ¥2.0 | ¥4.0 |
| viduq2 | ¥0.5 | ¥1.0 | ¥2.0 |
| viduq1 | ¥0.3 | ¥0.6 | ¥1.2 |

### 分辨率加成

- 480p: 无加成
- 720p: +20%
- 1080p: +50%

*具体价格以平台为准*

## 免费额度

- 每日免费 500 秒视频时长额度
- 适合开发测试

## 最佳实践

1. **模型选择**
   - 快速测试 → viduq3-turbo
   - 平衡质量 → viduq3
   - 高质量 → viduq2-pro
   - 预算有限 → viduq1

2. **智能切镜**: 需要多角度展示时使用 Q3 系列

3. **音画同出**: 需要音效时使用 viduq3-turbo

4. **首尾帧**: 需要精确控制起止画面时使用首尾帧模式

5. **多图参考**: 风格融合、元素组合使用多图参考

6. **参考视频**: 需要模仿特定视频风格时使用 Q2-pro

7. **提示词优化**: 
   - 使用中文描述
   - 最多 ~1500 字符，充分描述细节
   - 明确描述动作、场景、镜头

8. **时长选择**: 建议从 4s 开始测试，确认效果后升级到 8s/16s

9. **分辨率**: 测试用 480p，正式用 720p，展示用 1080p

10. **异步轮询**: 建议 10-15 秒轮询一次状态 (生成较慢)

## 与其他厂商对比

| 能力 | Vidu | MiniMax | 快手可灵 | 智谱 |
|------|------|---------|---------|------|
| 文生视频 | ✅ | ✅ | ✅ | ✅ |
| 图生视频 | ✅ | ✅ | ✅ | ✅ |
| 首尾帧 | ✅ | ✅ | ✅ | ❌ |
| 多图参考 | ✅ | ❌ | ❌ | ❌ |
| 参考视频 | ✅ (Q2-pro) | ✅ | ❌ | ❌ |
| 智能切镜 | ✅ (Q3) | ❌ | ✅ | ❌ |
| 音画同出 | ✅ (Q3-turbo) | ✅ | ✅ | ❌ |
| 视频编辑 | ✅ (Q2-pro) | ❌ | ❌ | ❌ |
| 最长时长 | 16s | 10s | 10s | 10s |
| 图片生成 | ❌ | ✅ | ✅ | ✅ |
| 中文优化 | ✅ | ✅ 强 | ✅ 强 | ✅ 强 |
| CLI 支持 | ❌ (社区) | ❌ | ❌ | ❌ |

## 接入建议

- **优先级**: 中优先级 (专注视频，多图参考、参考视频独特)
- **接入方式**: 自建技能 CLI
- **适用场景**: 视频创作、动画制作、风格迁移
- **优势**: 
  - 专注视频生成
  - 多图参考、参考视频能力独特
  - Q2-pro 支持视频编辑
  - 最长支持 16s
  - 免费额度充足
- **劣势**:
  - 不支持图片生成（需配合其他服务）
  - 生成速度相对较慢
  - 不支持精确摄像机控制
