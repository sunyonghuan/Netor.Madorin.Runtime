# 模型 HTTP 工具契约

> 当前生效版本：Madorin.AI.Runtime V1
>
> 本文描述 Runtime 暴露给模型的 HTTP 请求工具 `mcli_http`。工具在策略约束下发起 HTTP 请求，不绕过网络授权。

## 1. 工具

| Tool ID | Description | 风险 | 主要权限 |
| --- | --- | --- | --- |
| `mcli_http` | `Sends an allowed HTTP request.` | 网络 | `network.access`，受 NetworkPolicy 约束 |

## 2. 请求参数

```json
{
  "method": "GET",
  "url": "https://api.example.com/data",
  "headers": { "Accept": "application/json" },
  "body": null
}
```

| 参数 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `method` | string | 是 | `GET`、`HEAD`、`POST`、`PUT`、`PATCH`、`DELETE` |
| `url` | string | 是 | 绝对 URI，只接受 `http` 或 `https` 方案 |
| `headers` | object | 否 | 请求头键值对，最多 64 个 |
| `body` | string | 否 | 请求正文，UTF-8 编码后不超过 1 MB |

禁止设置的请求头：`connection`、`content-length`、`expect`、`host`、`keep-alive`、`proxy-authenticate`、`proxy-authorization`、`te`、`trailer`、`transfer-encoding`、`upgrade`。这些由 Runtime 控制。用户显式提供其中任一请求头会抛出 `ArgumentException` 使整个请求失败，而非被静默忽略或接管。

请求头值必须是字符串，长度不超过 8192，不能包含 `\r` 或 `\n`。

## 3. 响应格式

```json
{
  "statusCode": 200,
  "headers": {
    "content-type": "application/json; charset=utf-8"
  },
  "body": "{\"result\": \"ok\"}",
  "mediaType": "application/json"
}
```

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `statusCode` | integer | HTTP 状态码 |
| `headers` | object | 响应头，收集阶段最多 128 个，单个值不超过 8192；非文本响应会追加 `x-madorin-body-encoding`，输出可能出现 129 个头 |
| `body` | string | 文本类型返回原文；非文本类型返回 Base64 |
| `mediaType` | string? | 响应的 Content-Type 媒体类型，无则为 `null` |

文本类型的判定：`text/*`、含 `json`、`xml`、`javascript` 的媒体类型视为文本。128 个上限在响应头收集阶段校验；判定为非文本后再追加 `x-madorin-body-encoding: base64`，因此非文本响应的输出头数量可能达到 129 个。

响应正文默认上限 1 MB，NetworkPolicy 可调整至最高 4 MB。Content-Length 超过限制时直接失败，不会截断后返回。

## 4. 网络策略（NetworkPolicy）

`mcli_http` 的每次调用都受 Grant 附带的 NetworkPolicy 约束。策略为空（JSON 省略字段）时实际拒绝一切请求：`denyAll` 反序列化默认为 `true`，直接抛“Network access is denied”；即便显式将 `denyAll` 设为 `false`，`allowedHosts` 为空时主机匹配恒为假，请求仍被拒绝。要放行请求必须显式配置 `allowedHosts`。

| 字段 | 类型 | 默认 | 说明 |
| --- | --- | --- | --- |
| `denyAll` | boolean | `true` | 为 `true` 时禁止所有网络访问；JSON 省略该字段反序列化为 `true` |
| `allowedSchemes` | string[]? | `null` | 允许的 URI 方案；字段默认为 `null`，执行期回退为 `["https"]` |
| `allowedHosts` | string[]? | `null` | 允许的主机名，支持 `*.example.com` 通配；字段默认为 `null`，执行期按空集合处理（不匹配任何主机） |
| `allowedPorts` | int[]? | `null` | 允许的端口；`null` 时只允许默认端口（80/443） |
| `allowedAddressRanges` | string[]? | `null` | 允许的 CIDR 范围；`null` 时只允许公共地址 |
| `maxRedirects` | int? | `null` | 最大重定向次数，0-10；`null` 时执行期回退为 `0` |
| `maxResponseBytes` | long? | `null` | 响应正文上限；字段默认为 `null`，执行期回退为 1 MB，最高 4 MB |

### 4.1 主机匹配

- 精确匹配：`api.example.com` 只匹配同名主机。
- 通配匹配：`*.example.com` 匹配 `foo.example.com`，不匹配 `example.com` 本身。
- 匹配忽略尾部 `.` 和大小写。

### 4.2 地址验证

DNS 解析后，逐个判定每个解析出的 IP 是否为授权地址。满足以下条件之一即视为授权：

- `allowedAddressRanges` 中有匹配的 CIDR 范围；
- 未配置 CIDR 范围时，地址必须是公共地址。

以下 IPv4 范围被视为非公共并默认拒绝：`0.0.0.0/8`、`10.0.0.0/8`、`127.0.0.0/8`、`100.64.0.0/10`、`169.254.0.0/16`、`172.16.0.0/12`、`192.168.0.0/16`、`192.0.0.0/24`、`192.0.2.0/24`、`198.18.0.0/15`、`198.51.100.0/24`、`203.0.113.0/24`，以及多播（`224.0.0.0+`）。

IPv6 拒绝环回、链路本地、多播、`::/128`、`fc00::/7`（ULA）和 `2001:db8::/32`（文档保留）。

解析出的 IP 中只要至少有一个通过上述判定即视为授权，请求继续；一个都没有才失败。未通过判定的 IP 会被丢弃，后续连接只使用授权 IP（见 §4.3）。

### 4.3 DNS 固定

DNS 在 Prepare 阶段解析，连接时只使用已授权的 IP 地址。ConnectCallback 会验证目标主机和端口与已验证端点一致，防止 DNS 重绑定。

## 5. 重定向

- 默认不跟随重定向（`maxRedirects = 0`）。
- 支持 301、302、303、307、308。
- 每次重定向都重新验证目标 URI 和 IP 地址。
- 跨域重定向（scheme+host+port 变化）时自动删除 `authorization`、`cookie`、`proxy-authorization` 请求头。
- 303 或 301/302 + POST 时，方法降级为 GET 并丢弃 body。

## 6. 连接约束

- 不使用代理（`UseProxy = false`）。
- 不使用 Cookie（`UseCookies = false`）。
- 不自动解压（`AutomaticDecompression = None`）。
- 连接超时 10 秒；整体超时由 Run 的 CancellationToken 控制。
- URI 不能包含用户信息（`user:pass@host`）。
- 支持的响应编码：`utf-8`、`us-ascii`、`iso-8859-1`、`utf-16`、`utf-16be`；其他编码返回错误。

## 7. 边界

`mcli_http` 不支持 WebSocket、gRPC、HTTP/2 Server Push、流式响应或分块上传。需要这些能力时，由宿主提供专用工具。
