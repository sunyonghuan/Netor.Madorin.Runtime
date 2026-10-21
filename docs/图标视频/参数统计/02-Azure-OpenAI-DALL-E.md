# Azure OpenAI DALL-E 图片生成 API

## 基本信息

- **厂商**: Microsoft Azure OpenAI Service
- **产品**: DALL-E / GPT-Image 系列
- **端点**: `https://{resource-name}.openai.azure.com/openai/deployments/{deployment-id}/images/generations?api-version=2024-10-21`
- **方法**: POST
- **文档**: https://learn.microsoft.com/en-us/rest/api/azureopenai/images/generate
- **注意**: dall-e-3 已于 2026-03-04 退役，当前使用 `gpt-image-*` 系列模型

## 认证

```
api-key: YOUR_AZURE_OPENAI_API_KEY
```

或使用 Azure AD 令牌：

```
Authorization: Bearer YOUR_AZURE_AD_TOKEN
```

## URL 结构

```
https://{your-resource-name}.openai.azure.com/openai/deployments/{deployment-id}/images/generations?api-version={api-version}
```

- `{your-resource-name}`: Azure OpenAI 资源名称
- `{deployment-id}`: 部署 ID（在 Azure Portal 中创建）
- `{api-version}`: API 版本（当前推荐 `2024-10-21`）

## 请求参数

### 核心参数

| 参数 | 类型 | 必需 | 说明 | 默认值 |
|------|------|------|------|--------|
| `prompt` | string | ✅ | 图片描述文本（最多 4000 字符） | - |
| `size` | string | ❌ | 图片尺寸 | `1024x1024` |
| `quality` | string | ❌ | 图片质量 | `standard` |
| `style` | string | ❌ | 风格 | `vivid` |
| `n` | integer | ❌ | 生成图片数量（仅支持 1） | 1 |
| `response_format` | string | ❌ | 响应格式 | `url` |

### 支持的尺寸

- `1024x1024`
- `1792x1024`
- `1024x1792`

### 支持的质量

- `standard`: 标准质量
- `hd`: 高清质量（更精细，更昂贵）

### 支持的风格

- `vivid`: 鲜艳生动
- `natural`: 自然真实

### 响应格式

- `url`: 返回图片 URL
- `b64_json`: 返回 Base64 编码的图片

## 请求示例

```json
{
  "prompt": "A serene landscape with mountains and a lake at sunset",
  "size": "1024x1024",
  "quality": "hd",
  "style": "natural",
  "n": 1
}
```

## 响应格式

```json
{
  "created": 1702839645,
  "data": [
    {
      "url": "https://{resource-name}.openai.azure.com/...",
      "revised_prompt": "A peaceful mountain landscape..."
    }
  ]
}
```

### 响应字段

| 字段 | 类型 | 说明 |
|------|------|------|
| `created` | integer | Unix 时间戳 |
| `data` | array | 生成结果数组 |
| `data[].url` | string | 图片 URL |
| `data[].b64_json` | string | Base64 编码的图片 |
| `data[].revised_prompt` | string | 修订后的提示词 |

## 错误码

| HTTP 状态码 | 错误类型 | 说明 |
|-------------|----------|------|
| 400 | Bad Request | 参数错误或部署不支持图片生成 |
| 401 | Unauthorized | API Key 无效或未授权 |
| 404 | Not Found | 部署 ID 不存在 |
| 429 | Too Many Requests | 速率限制超限 |
| 500 | Internal Server Error | Azure 服务端错误 |

## 与 OpenAI 的差异

### 相同点

- 参数结构完全一致
- 响应格式相同
- 支持的尺寸、质量、风格相同

### 不同点

1. **端点结构**
   - OpenAI: `/v1/images/generations`
   - Azure: `/openai/deployments/{deployment-id}/images/generations?api-version=...`

2. **认证方式**
   - OpenAI: `Authorization: Bearer YOUR_API_KEY`
   - Azure: `api-key: YOUR_AZURE_OPENAI_API_KEY` 或 Azure AD 令牌

3. **模型指定**
   - OpenAI: 在请求体中指定 `model: "dall-e-3"`
   - Azure: 通过 URL 路径中的 `deployment-id` 指定

4. **部署要求**
   - Azure 需要在 Portal 中预先创建部署
   - 部署绑定特定模型和配额

5. **定价**
   - Azure 按 Azure 订阅计费，价格可能与 OpenAI 不同
   - 受区域和配额影响

## 公共参数抽取

### 与 OpenAI 通用参数

- ✅ `prompt`
- ✅ `size`
- ✅ `quality`
- ✅ `style`
- ✅ `n` (固定为 1)
- ✅ `response_format`

### Azure 特有配置

- ✅ `resource_name` (资源名称)
- ✅ `deployment_id` (部署 ID)
- ✅ `api_version` (API 版本)

## 特殊能力

- 支持 Azure Private Link 进行私有网络访问
- 支持 Azure AD 身份验证
- 受 Azure 配额和速率限制管理
- 图片 URL 有效期依赖 Azure 存储配置

## 最佳实践

1. **部署管理**: 为不同环境创建独立部署（开发、测试、生产）
2. **错误处理**: 捕获 404 错误（部署不存在）和 429 错误（配额耗尽）
3. **监控**: 使用 Azure Monitor 跟踪 API 使用情况和错误率
4. **成本优化**: 优先使用 `standard` 质量，仅在需要时使用 `hd`

## 迁移注意事项

从 OpenAI 迁移至 Azure OpenAI 时：

1. 修改端点 URL 结构
2. 更换认证方式（Bearer Token → api-key Header）
3. 移除请求体中的 `model` 字段（改为 URL 路径中的 deployment-id）
4. 在 Azure Portal 中创建图片生成部署
5. 调整错误处理逻辑（Azure 特有的 404 错误）
