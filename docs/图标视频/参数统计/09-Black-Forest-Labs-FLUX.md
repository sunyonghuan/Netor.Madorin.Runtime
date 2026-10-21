# Black Forest Labs FLUX API

## 基本信息

- **厂商**: Black Forest Labs (BFL)
- **产品**: FLUX.1, FLUX.2 系列
- **端点**: `https://api.bfl.ml/v1/*`
- **文档**: https://docs.bfl.ai/
- **模型**: FLUX.1 [pro], FLUX.1 [dev], FLUX.2

## 认证

```
X-Key: YOUR_BFL_API_KEY
```

## 主要端点

### FLUX.1 [pro] - 文生图

```
POST https://api.bfl.ml/v1/flux-pro
```

### FLUX.1.1 [pro] - 快速版

```
POST https://api.bfl.ml/v1/flux-pro-1.1
```

### FLUX.2 [pro] - 最新版

```
POST https://api.bfl.ml/v1/flux-pro-2
```

### FLUX.1 [dev] - 开发版

```
POST https://api.bfl.ml/v1/flux-dev
```

## 核心请求参数

| 参数 | 类型 | 必需 | 说明 | 默认值 |
|------|------|------|------|--------|
| `prompt` | string | ✅ | 文本描述 (最多 10,000 字符) | - |
| `width` | integer | ❌ | 宽度 (256-1440, 32 倍数) | 1024 |
| `height` | integer | ❌ | 高度 (256-1440, 32 倍数) | 1024 |
| `prompt_upsampling` | boolean | ❌ | 自动优化提示词 | false |
| `seed` | integer | ❌ | 随机种子 | 随机 |
| `safety_tolerance` | integer | ❌ | 安全容忍度 (0-6) | 2 |
| `output_format` | string | ❌ | 输出格式 | `jpeg` |

## FLUX.2 特有参数

| 参数 | 类型 | 说明 | 默认值 |
|------|------|------|--------|
| `guidance` | float | 引导强度 (2.0-5.0) | 3.0 |
| `steps` | integer | 推理步数 (1-50) | 25 |
| `interval` | float | 时间步间隔 (1.0-4.0) | 2.0 |
| `reference_images` | array | 参考图片 (最多 10 张) | - |

## FLUX.1 [dev] 特有参数

| 参数 | 类型 | 说明 | 默认值 |
|------|------|------|--------|
| `guidance` | float | 引导强度 (1.5-5.0) | 3.0 |
| `steps` | integer | 推理步数 (1-50) | 28 |

## 输出格式选项

- `jpeg`: JPEG 图片
- `png`: PNG 图片
- `webp`: WebP 图片

## 安全容忍度

| 值 | 说明 |
|----|------|
| 0 | 最严格 |
| 2 | 默认（推荐） |
| 6 | 最宽松 |

## 请求示例

### FLUX.1 [pro]

```json
{
  "prompt": "a serene mountain landscape with a lake",
  "width": 1024,
  "height": 1024,
  "prompt_upsampling": false,
  "seed": 12345,
  "safety_tolerance": 2,
  "output_format": "jpeg"
}
```

### FLUX.2 [pro] 带参考图

```json
{
  "prompt": "transform this into a cyberpunk style",
  "width": 1024,
  "height": 1024,
  "guidance": 3.5,
  "steps": 30,
  "reference_images": [
    {
      "image_url": "https://example.com/ref1.jpg",
      "weight": 1.0
    },
    {
      "image_url": "https://example.com/ref2.jpg",
      "weight": 0.8
    }
  ],
  "seed": 42
}
```

### FLUX.1 [dev]

```json
{
  "prompt": "a futuristic cityscape at night",
  "width": 1344,
  "height": 768,
  "guidance": 3.0,
  "steps": 28,
  "seed": 999
}
```

## 响应格式

### 提交响应

```json
{
  "id": "task_abc123"
}
```

### 查询状态

```
GET https://api.bfl.ml/v1/get_result?id=task_abc123
```

### 完成响应

```json
{
  "id": "task_abc123",
  "status": "Ready",
  "result": {
    "sample": "https://cdn.bfl.ml/abc123.jpg",
    "prompt": "a serene mountain landscape..."
  }
}
```

### 进行中响应

```json
{
  "id": "task_abc123",
  "status": "Pending"
}
```

或

```json
{
  "id": "task_abc123",
  "status": "Running"
}
```

### 失败响应

```json
{
  "id": "task_abc123",
  "status": "Error",
  "error": "Content policy violation"
}
```

## 状态枚举

| 状态 | 说明 |
|------|------|
| `Pending` | 等待中 |
| `Running` | 生成中 |
| `Ready` | 完成 |
| `Error` | 失败 |

## 错误码

| HTTP 状态码 | 说明 |
|-------------|------|
| 400 | 参数错误 |
| 401 | API Key 无效 |
| 402 | 余额不足 |
| 403 | 内容违规 |
| 429 | 速率限制 |
| 500 | 服务端错误 |

## 公共参数抽取

### 图片生成通用参数

- ✅ `prompt` (文本提示词)
- ✅ `width` / `height` (尺寸, 灵活)
- ✅ `seed` (随机种子) ⭐
- ✅ `guidance` (引导强度, FLUX.2/dev) ⭐
- ✅ `steps` (推理步数, FLUX.2/dev) ⭐
- ✅ `reference_images` (参考图片, FLUX.2) ⭐
- ✅ `prompt_upsampling` (提示词优化) ⭐
- ✅ `safety_tolerance` (安全容忍度) ⭐
- ✅ `output_format` (输出格式)
- ❌ `negative_prompt` (不支持)
- ❌ `style` (无预设风格)
- ❌ `n` (不支持批量)

⭐ 表示 OpenAI 不支持的能力

## 特殊能力

### Black Forest Labs 独有

- ✅ **FLUX 系列**: 业界领先的文生图质量
- ✅ **灵活尺寸**: 256-1440 任意尺寸（32 倍数）
- ✅ **提示词优化**: `prompt_upsampling` 自动增强
- ✅ **参考图片**: FLUX.2 支持最多 10 张参考图
- ✅ **安全容忍度**: 可调节内容过滤严格程度
- ✅ **多格式输出**: JPEG/PNG/WebP
- ✅ **高分辨率**: 支持 4MP 输出（FLUX.2）
- ✅ **引导控制**: 精细调节生成过程
- ❌ **无负面提示词**: 不支持 negative_prompt
- ❌ **无批量生成**: 一次只生成一张

### 版本对比

| 能力 | FLUX.1 [pro] | FLUX.1.1 [pro] | FLUX.2 [pro] | FLUX.1 [dev] |
|------|--------------|----------------|--------------|--------------|
| 质量 | 高 | 高 | 最高 | 中 |
| 速度 | 中 | 快 | 中 | 慢 |
| 参考图 | ❌ | ❌ | ✅ (10 张) | ❌ |
| 最大分辨率 | 1440² | 1440² | 4MP | 1440² |
| 文字渲染 | 良好 | 良好 | 优秀 | 一般 |
| 引导控制 | ❌ | ❌ | ✅ | ✅ |
| 步数控制 | ❌ | ❌ | ✅ | ✅ |
| 定价 | 高 | 中 | 最高 | 低 |

### 与其他厂商对比

| 能力 | BFL FLUX | OpenAI | Stability AI | xAI |
|------|----------|--------|--------------|-----|
| 质量 | ✅ 最高 | 高 | 中-高 | 高 |
| 速度 | 中 | 快 | 快 | 中 |
| 灵活尺寸 | ✅ | ❌ | ✅ | ❌ |
| 参考图片 | ✅ (10 张) | ❌ | ✅ (1 张) | ✅ (5 张) |
| 文字渲染 | ✅ 优秀 | 良好 | 一般 | 良好 |
| 提示词优化 | ✅ | ✅ | ❌ | ✅ |
| 负面提示词 | ❌ | ❌ | ✅ | ❌ |
| 随机种子 | ✅ | ❌ | ✅ | ❌ |

## 定价（参考）

| 模型 | 价格 | 速度 | 推荐场景 |
|------|------|------|----------|
| FLUX.1 [pro] | $0.055 / 图 | 10-15s | 高质量生产 |
| FLUX.1.1 [pro] | $0.040 / 图 | 5-8s | 快速迭代 |
| FLUX.2 [pro] | $0.080 / 图 | 12-18s | 最高质量 + 参考图 |
| FLUX.1 [dev] | $0.025 / 图 | 15-20s | 开发测试 |

## 最佳实践

1. **版本选择**
   - 快速测试 → FLUX.1.1 [pro]
   - 高质量 → FLUX.1 [pro]
   - 参考图编辑 → FLUX.2 [pro]
   - 开发环境 → FLUX.1 [dev]

2. **尺寸优化**: 使用 32 的倍数，推荐：
   - `1024x1024` (1:1)
   - `1344x768` (16:9)
   - `768x1344` (9:16)
   - `1216x832` (3:2)

3. **提示词优化**
   - 启用 `prompt_upsampling` 可提升质量
   - FLUX 对自然语言提示词理解能力强
   - 描述越详细效果越好

4. **参考图使用** (FLUX.2)
   - 1-3 张参考图效果最佳
   - 使用 `weight` 控制每张图的影响
   - 参考图用于风格迁移效果明显

5. **引导控制** (FLUX.2/dev)
   - `guidance=2.5-3.5`: 平衡创造性与提示词遵循
   - `guidance=4.0-5.0`: 更严格遵循提示词
   - `guidance=1.5-2.0`: 更自由创造

6. **步数控制** (FLUX.2/dev)
   - `steps=15-20`: 快速预览
   - `steps=25-30`: 标准质量
   - `steps=40-50`: 最高质量（收益递减）

7. **安全容忍度**
   - 艺术创作 → `safety_tolerance=4-6`
   - 商业用途 → `safety_tolerance=2`（默认）
   - 儿童内容 → `safety_tolerance=0`

8. **种子复现**: 使用相同的 `seed`, `width`, `height`, `prompt` 可精确复现

9. **轮询间隔**: 建议 2-3 秒轮询一次状态

10. **输出格式**
    - WebP: 最小文件大小
    - PNG: 无损质量
    - JPEG: 平衡选择

## Python SDK 示例

```python
import requests
import time

# 提交任务
response = requests.post(
    "https://api.bfl.ml/v1/flux-pro-2",
    headers={"X-Key": "YOUR_API_KEY"},
    json={
        "prompt": "a serene landscape",
        "width": 1024,
        "height": 1024,
        "seed": 42
    }
)
task_id = response.json()["id"]

# 轮询结果
while True:
    result = requests.get(
        f"https://api.bfl.ml/v1/get_result?id={task_id}",
        headers={"X-Key": "YOUR_API_KEY"}
    ).json()
    
    if result["status"] == "Ready":
        print(result["result"]["sample"])
        break
    elif result["status"] == "Error":
        print(f"Error: {result['error']}")
        break
    
    time.sleep(2)
```

## 迁移注意事项

从 OpenAI 迁移至 BFL FLUX 时：

1. **异步任务**: 实现提交 + 轮询逻辑
2. **认证方式**: `Authorization: Bearer` → `X-Key:`
3. **响应结构**: `data[].url` → `result.sample`
4. **无批量生成**: 移除 `n` 参数
5. **尺寸约束**: 确保宽高是 32 的倍数
6. **无负面提示词**: 移除 `negative_prompt`
7. **种子支持**: 可添加 `seed` 参数实现复现
8. **参考图功能**: FLUX.2 支持，但格式不同
