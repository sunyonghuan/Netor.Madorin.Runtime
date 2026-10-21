# 图片/视频生成 API 参数统计文档库

## 文档说明

本目录收集了各大平台图片与视频生成 API 的详细参数文档，用于抽离公共参数设计技能契约。

**重要更新 (2026-10-20)**: 
- ✅ 已补充完整的国内外厂商信息（共 20+ 厂商）
- ✅ 包括快手可灵、MiniMax、生数 Vidu、爱诗 PixVerse、阶跃星辰等国内主流厂商
- ✅ 整理 CLI 支持情况，区分官方 CLI 与 HTTP API
- ✅ 创建厂商接入能力汇总文档

## 文档列表

### 📊 核心汇总文档

- **[厂商接入能力汇总.md](./厂商接入能力汇总.md)** - ⭐ **最新** 完整厂商列表、CLI 支持情况、接入优先级
- **[00-公共参数汇总.md](./00-公共参数汇总.md)** - 参数对比矩阵与技能契约设计建议

### 🖼️ 已收集参数的厂商 (9 个)

#### 国际厂商

1. **[01-OpenAI-DALL-E.md](./01-OpenAI-DALL-E.md)**
   - 厂商: OpenAI
   - 模型: DALL-E 2 / DALL-E 3
   - CLI: ❌ 仅 HTTP API
   - 特点: 行业标准，提示词自动优化
   - 推荐: 🔥 高优先级 (作为第一个模板技能)

2. **[02-Azure-OpenAI-DALL-E.md](./02-Azure-OpenAI-DALL-E.md)**
   - 厂商: Microsoft Azure
   - 模型: GPT-Image 系列
   - CLI: ❌ 仅 HTTP API
   - 特点: 与 OpenAI 兼容，Azure 基础设施

3. **[04-Stability-AI-Stable-Diffusion.md](./04-Stability-AI-Stable-Diffusion.md)**
   - 厂商: Stability AI
   - 模型: SD XL, SD 3.5, Stable Image Ultra
   - CLI: ❌ 仅 HTTP API
   - 特点: 负面提示词，随机种子，风格预设丰富
   - 推荐: 🔥 高优先级

4. **[05-xAI-Grok-Imagine.md](./05-xAI-Grok-Imagine.md)**
   - 厂商: xAI
   - 模型: Grok Imagine (图片) + Grok Video (视频)
   - CLI: ❌ 仅 HTTP API
   - 特点: 音效生成，对话生成，参考图编辑
   - 推荐: 🔥 高优先级

5. **[09-Black-Forest-Labs-FLUX.md](./09-Black-Forest-Labs-FLUX.md)**
   - 厂商: Black Forest Labs
   - 模型: FLUX.1, FLUX.2 系列
   - CLI: ❌ 仅 HTTP API
   - 特点: 业界最高质量，灵活尺寸，文字渲染优秀
   - 推荐: 🔥 高优先级

6. **[08-Replicate-聚合平台.md](./08-Replicate-聚合平台.md)**
   - 厂商: Replicate
   - 模型: 托管 100+ 开源模型 (FLUX, SDXL, SeedANCE, Kling, SVD...)
   - CLI: ❌ 仅 HTTP API
   - 特点: 统一 API，Webhook 支持，版本锁定
   - 推荐: 中优先级

#### 国内厂商

7. **[03-智谱-CogView-CogVideoX.md](./03-智谱-CogView-CogVideoX.md)**
   - 厂商: 智谱 AI
   - 模型: CogView-4 (图片), CogVideoX (视频)
   - CLI: ❌ 仅 HTTP API
   - 特点: 中文优化，图片 + 视频双支持
   - 推荐: 🔥 高优先级

8. **[06-阿里云-通义万相.md](./06-阿里云-通义万相.md)**
   - 厂商: 阿里云 DashScope
   - 模型: 通义万相 (Wanxiang)
   - CLI: ✅ `aliyun` (云厂商通用 CLI)
   - 特点: 中文深度优化，多端点（文生图/编辑/重绘），异步任务
   - 推荐: 🔥 高优先级 (已有 CLI)

9. **[07-腾讯云-混元生图.md](./07-腾讯云-混元生图.md)**
   - 厂商: 腾讯云
   - 模型: 混元生图 + HunyuanVideo
   - CLI: ✅ `tccli` (云厂商通用 CLI)
   - 特点: 三版本（极速/轻量/标准），中文优化，水印控制
   - 推荐: 🔥 高优先级 (已有 CLI)

### 📝 待收集参数的国内厂商 (6 个)

根据《厂商接入能力汇总.md》，以下厂商已确认有 API 但尚未收集详细参数:

10. **快手 - 可灵 / 可图**
    - 图片: 可图
    - 视频: 可灵
    - CLI: ❌ 仅 HTTP API
    - 推荐: 🔥 高优先级 (视频生成热门)

11. **MiniMax - 图像 / 海螺视频**
    - 图片 + 视频双支持
    - CLI: ❌ 仅 HTTP API
    - 推荐: 🔥 高优先级

12. **百度 - 文心一格 / 视频生成**
    - 图片 + 视频双支持
    - CLI: ✅ `bce` (云厂商通用 CLI)
    - 推荐: 🔥 高优先级 (已有 CLI)

13. **阶跃星辰 - Step-1X / Step-Video**
    - 图片 + 视频双支持
    - CLI: ❌ 仅 HTTP API
    - 推荐: 中优先级

14. **生数科技 - Vidu**
    - 视频专精
    - CLI: ❌ 仅 HTTP API
    - 推荐: 中优先级

15. **爱诗科技 - PixVerse**
    - 视频专精
    - CLI: ❌ 仅 HTTP API
    - 推荐: 低优先级 (国际站)

### ⚠️ 已有官方技能 CLI 的厂商

- **字节火山方舟 - Seedream / Seedance**
  - 图片: Seedream
  - 视频: Seedance 2.0/2.5
  - CLI: ✅ **`arkcli`** (AI 原生 CLI)
  - MCP: ✅ 官方 MCP Server
  - 状态: ✅ **已有技能模式 CLI,无需重复开发**

### 🔍 低优先级厂商 (暂不收集)

- 昆仑万维 (部分开放)
- 商汤 (偏 B 端私有化)
- 上海 AI 实验室 (开源为主)
- 美图 (偏 B 端)
- 潞晨科技 (Video Ocean 相对小众)
- RunwayML Gen-3 (视频,国际)
- Pika Labs (视频,国际)
- Leonardo.ai (图片,国际)
- Ideogram (图片,国际)

## 快速对比表

### 图片生成能力对比

| 厂商 | 负面提示词 | 随机种子 | 参考图 | 中文优化 | 异步任务 | CLI 支持 |
|------|-----------|---------|--------|---------|---------|----------|
| OpenAI | ❌ | ❌ | ❌ | 一般 | ❌ | ❌ |
| Azure | ❌ | ❌ | ❌ | 一般 | ❌ | ❌ |
| 智谱 | ❌ | ❌ | ❌ | ✅ 强 | ❌ | ❌ |
| Stability AI | ✅ | ✅ | ✅ | 弱 | ❌ | ❌ |
| xAI | ❌ | ❌ | ✅ (5张) | 一般 | ❌ | ❌ |
| 阿里云 | ✅ | ✅ | ✅ | ✅ 强 | ✅ | ✅ aliyun |
| 腾讯云 | ✅ | ✅ | ❌ | ✅ 强 | ✅ | ✅ tccli |
| 百度 | 待查 | 待查 | 待查 | ✅ 强 | 待查 | ✅ bce |
| BFL FLUX | ❌ | ✅ | ✅ (10张) | 一般 | ✅ | ❌ |
| Replicate | ✅ | ✅ | ✅ | 依模型 | ✅ | ❌ |
| **火山方舟** | 待查 | 待查 | 待查 | ✅ | ✅ | ✅ **arkcli** |
| 快手 | 待查 | 待查 | 待查 | ✅ 强 | 待查 | ❌ |
| MiniMax | 待查 | 待查 | 待查 | ✅ | 待查 | ❌ |
| 阶跃星辰 | 待查 | 待查 | 待查 | ✅ | 待查 | ❌ |

### 视频生成能力对比

| 厂商 | 文生视频 | 图生视频 | 音效 | CLI 支持 |
|------|---------|---------|------|----------|
| 智谱 CogVideoX | ✅ | ✅ | ❌ | ❌ |
| xAI Grok | ✅ | ✅ | ✅ | ❌ |
| 腾讯 HunyuanVideo | ✅ | ✅ | ❌ | ✅ tccli |
| 百度 | ✅ | 待查 | ❌ | ✅ bce |
| **火山 Seedance** | ✅ | ✅ | ❌ | ✅ **arkcli** |
| 快手可灵 | ✅ | ✅ | ❌ | ❌ |
| MiniMax 海螺 | ✅ | 待查 | ❌ | ❌ |
| 生数 Vidu | ✅ | ✅ | ❌ | ❌ |
| 爱诗 PixVerse | ✅ | ✅ | ❌ | ❌ |
| 阶跃 Step-Video | ✅ | 待查 | ❌ | ❌ |
| Replicate (SeedANCE) | ✅ | ✅ | ❌ | ❌ |
| Replicate (Kling) | ✅ | ✅ | ❌ | ❌ |

### CLI 支持情况汇总

| CLI 类型 | 厂商 | CLI 名称 | 特点 |
|---------|------|---------|------|
| **AI 原生 CLI** | 字节火山方舟 | `arkcli` | ✅ 专为生成式 AI 设计 <br> ✅ MCP Server 支持 <br> ✅ 已有技能模式 |
| **云厂商通用 CLI** | 阿里云 | `aliyun` | 通用云 CLI,子命令暴露 |
| **云厂商通用 CLI** | 腾讯云 | `tccli` | 通用云 CLI,TC3 签名 |
| **云厂商通用 CLI** | 百度云 | `bce` | 通用云 CLI |
| **仅 HTTP API** | 其余所有 | - | 需自建技能 CLI |

## 公共参数统计

### 核心参数 (100% 覆盖)

- ✅ `prompt` - 文本描述
- ✅ 尺寸控制 - `size` / `aspect_ratio` / `width+height` (三种模式)
- ✅ `model` - 模型名称 (88%)

### 高频参数 (50%+ 覆盖)

- ✅ `num_images` - 生成数量 (66%)
- ✅ `response_format` - url / base64 (55%)
- ✅ `style` - 风格预设 (44%)

### 专业参数 (部分厂商)

- ✅ `negative_prompt` - 负面提示词 (44%)
- ✅ `seed` - 随机种子 (55%)
- ✅ `reference_images` - 参考图片 (55%)
- ✅ `guidance_scale` - 引导强度 (22%)
- ✅ `steps` - 推理步数 (22%)

### 视频特有参数

- ✅ `duration` - 时长（秒）
- ✅ `resolution` - 分辨率
- ✅ `fps` - 帧率 (66%)
- ✅ `image_url` - 图生视频输入
- ✅ `aspect_ratio` - 宽高比 (66%)

详见 `00-公共参数汇总.md`。

## 使用指南

### 1. 查看厂商接入能力

首先阅读 **[厂商接入能力汇总.md](./厂商接入能力汇总.md)** 了解:
- 完整厂商列表（20+ 厂商）
- CLI 支持情况
- 接入优先级建议
- MCP Server 支持情况

### 2. 查看详细 API 参数

打开对应厂商的 `.md` 文件查看:
- 端点地址
- 认证方式
- 请求参数
- 响应格式
- 错误码
- 特殊能力

### 3. 设计技能契约

参考 **[00-公共参数汇总.md](./00-公共参数汇总.md)**:
- 公共参数设计建议
- 参数映射策略
- 技能内部适配策略

### 4. 实现技能 CLI

按优先级实现:
1. 第一批: OpenAI (模板) + 已有 CLI 的云厂商
2. 第二批: 热门国内厂商 (智谱、快手、MiniMax)
3. 第三批: 国际高质量厂商 (xAI, Stability, BFL)

## 技能开发优先级

### ✅ 立即行动

1. **确认火山方舟 `arkcli` 可用性**
   - 检查是否已安装
   - 注册到 mcli_process allowlist

### 🔥 第一批 (基准与已有 CLI)

2. **OpenAI DALL-E** - 作为第一个模板技能
3. **阿里云通义万相** - 已有 `aliyun` CLI
4. **腾讯云混元** - 已有 `tccli` CLI
5. **百度文心一格** - 已有 `bce` CLI

### 🔥 第二批 (热门国内)

6. **智谱 CogView / CogVideoX** - 图片 + 视频
7. **快手可灵 / 可图** - 视频热门
8. **MiniMax** - 图片 + 视频

### 🔥 第三批 (国际高质量)

9. **xAI Grok Imagine** - 音效 + 对话
10. **Stability AI** - 负面提示词 + 种子
11. **BFL FLUX** - 最高质量

### 📌 第四批 (补充)

12. 阶跃星辰 Step-1X / Step-Video
13. 生数 Vidu
14. Replicate 聚合平台

## 下一步工作

- [ ] 补充快手、MiniMax、百度、阶跃、生数、PixVerse 的详细 API 参数文档
- [ ] 确认火山方舟 `arkcli` 可用性并注册 allowlist
- [ ] 创建 API 端点对比表（端点格式、认证方式、高级能力）
- [ ] 设计统一技能契约 JSON Schema
- [ ] 实现第一个模板技能 CLI (OpenAI DALL-E)

## 贡献指南

新增厂商文档时，请按以下结构编写：

```markdown
# 厂商名称 产品名称 API

## 基本信息
- 厂商、产品、端点、文档链接、CLI 支持情况

## 认证
认证方式说明

## 请求参数
详细参数表格

## 响应格式
JSON 示例

## 错误码
错误码表

## 公共参数抽取
可映射的公共参数

## 特殊能力
独有能力说明

## 定价
价格参考

## 最佳实践
使用建议
```

## 更新日志

- **2026-10-20 (晚)**:
  - ✅ 创建《厂商接入能力汇总.md》，补充完整国内外厂商信息（20+ 厂商）
  - ✅ 整理 CLI 支持情况，区分 AI 原生 CLI、云厂商通用 CLI、仅 HTTP API
  - ✅ 明确火山方舟已有官方技能 CLI,无需重复开发
  - ✅ 文档迁移至 `参数统计/` 文件夹

- **2026-10-20 (早)**:
  - ✅ 创建文档库，收集 9 个厂商的图片/视频生成 API 文档
  - ✅ 完成公共参数汇总分析
  - ✅ 创建参数对比矩阵
