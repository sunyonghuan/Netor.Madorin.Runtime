# 阶跃星辰 Step-1X / Step-Video API

## 基本信息

- **厂商**: 阶跃星辰 (StepFun)
- **产品**: Step-1X (图片), Step-Video (视频)
- **官网**: https://www.stepfun.com
- **开放平台**: https://platform.stepfun.com
- **CLI**: ❌ 仅 HTTP API
- **文档**: https://platform.stepfun.com/docs
- **开源模型**: GitHub - stepfun-ai

## 图片生成 API (Step-1X)

### 核心请求参数

| 参数 | 类型 | 必需 | 说明 | 默认值 |
|------|------|------|------|--------|
| `prompt` | string | ✅ | 文本描述 | - |
| `negative_prompt` | string | ❌ | 负面提示词 | - |
| `width` | integer | ❌ | 宽度 | 1024 |
| `height` | integer | ❌ | 高度 | 1024 |
| `num_images` | integer | ❌ | 生成数量 | 1 |
| `seed` | integer | ❌ | 随机种子 | 随机 |
| `model` | string | ❌ | 模型版本 | `step-1x` |

### 请求示例

```json
{
  "model": "step-1x",
  "prompt": "一幅山水画，水墨风格",
  "negative_prompt": "模糊，低质量",
  "width": 1024,
  "height": 1024,
  "seed": 42
}
```

## 视频生成 API (Step-Video)

### 模型版本

- `step-video-t2v`: 文生视频 (30B 参数)
- `step-video-ti2v`: 文本+图片生成视频

### 核心请求参数

| 参数 | 类型 | 必需 | 说明 | 默认值 |
|------|------|------|------|--------|
| `prompt` | string | ✅ | 文本描述 | - |
| `model` | string | ❌ | 模型版本 | `step-video-t2v` |
| `image_url` | string | ❌ | 输入图片 (ti2v) | - |
| `duration` | integer | ❌ | 时长（秒） | 5 |
| `num_frames` | integer | ❌ | 帧数（最多 204） | 120 |
| `resolution` | string | ❌ | 分辨率 | `720p` |

### 特点

- 支持最多 **204 帧**生成
- 30B 参数模型
- 中文优化

### 请求示例

```json
{
  "model": "step-video-t2v",
  "prompt": "一只小猫在草地上玩耍",
  "duration": 5,
  "num_frames": 120,
  "resolution": "720p"
}
```

## 第三方接入

- **阿里云百炼**: 可通过阿里云 Model Studio 接入
- **ComfyUI**: ComfyUI_StepFun 节点支持
- **Hugging Face**: stepfun-ai 模型库

## 认证

```
Authorization: Bearer YOUR_API_KEY
```

## 公共参数

### 图片
- ✅ prompt, negative_prompt, width, height, seed
- ❌ 风格预设

### 视频
- ✅ prompt, image_url, duration, num_frames
- ❌ 摄像机控制

## 接入建议

- **优先级**: 中优先级
- **接入方式**: 自建技能 CLI
- **优势**: 中文优化，开源模型可本地部署
