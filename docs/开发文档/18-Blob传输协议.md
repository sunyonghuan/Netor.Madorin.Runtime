# Blob 传输协议

> 当前生效版本：Madorin.AI.Runtime V1
>
> 本文描述 Runtime 的二进制内容传输协议。Blob 用于在 Runtime 与宿主/CLI 之间传输大型二进制内容，以 SHA-256 哈希寻址，扁平存储在 `blobs/` 目录，文件名为 `<SHA-256>.blob`。

## 1. 概述

Blob 传输处理 Runtime 与宿主之间的大型二进制内容交换。典型场景包括工具执行结果（截图、编译产物）、文件附件和大型文本输出。

设计原则：

- **内容寻址**：以 SHA-256 哈希作为 Blob ID，相同内容去重。
- **分块传输**：大文件拆分为多个 chunk 传输，支持完整性校验。
- **原子发布**：写入 staging 目录，校验通过后原子移动到 blobs 目录。
- **跨 Session 共享**：Blob 不绑定特定 Session，多个 Session 可引用同一 Blob。

## 2. 存储结构

```text
<workspace>/.madorin/
├── blobs/
│   ├── abcdef1234....blob   ← <SHA-256>.blob，扁平存放
│   └── cdef567890....blob
└── staging/
    ├── 3f2a1b....part       ← <uploadId>.part，进行中的传输
    └── 9c4e07....part
```

| 目录 | 说明 |
| --- | --- |
| `blobs/` | 已完成的 Blob 存储，所有文件扁平放在此目录下，无前缀子目录，文件名为 `<SHA-256>.blob` |
| `staging/` | 进行中的传输，每次上传对应一个 `<uploadId>.part` 文件，`uploadId` 为去横线的 32 位十六进制 GUID |

写入流程：

1. 在 `staging/` 下创建单个 `<uploadId>.part` 文件。
2. 各数据块以追加方式写入同一 `.part` 文件。
3. 增量计算全部内容的 SHA-256。
4. 校验通过后原子移动到 `blobs/<hash>.blob`。
5. 清理 `staging/` 中的 `.part` 文件。

## 3. 传输协议

Blob 契约定义在 `Contracts/Blob/` 目录。

### 3.1 BlobBeginRequest

开始上传。

```csharp
public sealed record BlobBeginRequest(
    string UploadId,
    string RunId,
    string ContentType,
    string AccessScope,
    DateTimeOffset ExpiresAt,
    long? Length = null);
```

| 字段 | 说明 |
| --- | --- |
| `UploadId` | 上传唯一标识 |
| `RunId` | 关联的 Run |
| `ContentType` | MIME 类型 |
| `AccessScope` | 访问范围 |
| `ExpiresAt` | 过期时间 |
| `Length` | 预期总长度（可选） |

### 3.2 BlobChunkRequest

上传数据块。

```csharp
public sealed record BlobChunkRequest(string UploadId);
```

数据块通过传输层帧的数据部分携带，`UploadId` 标识所属上传。

### 3.3 BlobCompleteRequest

完成上传，触发校验和发布。

```csharp
public sealed record BlobCompleteRequest(
    string UploadId,
    long Length,
    string Sha256);
```

| 字段 | 说明 |
| --- | --- |
| `UploadId` | 上传标识 |
| `Length` | 实际总字节数 |
| `Sha256` | 全部内容的 SHA-256 哈希 |

### 3.4 BlobReadRequest

读取已存储的 Blob。

```csharp
public sealed record BlobReadRequest(
    string BlobId,
    long Offset,
    int MaxBytes);
```

| 字段 | 说明 |
| --- | --- |
| `BlobId` | Blob 标识（SHA-256） |
| `Offset` | 读取起始偏移 |
| `MaxBytes` | 最大返回字节数 |

### 3.5 BlobOperationResponse

所有写操作和读操作的统一响应。

```csharp
public sealed record BlobOperationResponse(
    bool Success,
    string? Error,
    BlobReference? Reference,
    long Offset,
    bool EndOfBlob);
```

| 字段 | 说明 |
| --- | --- |
| `Success` | 操作是否成功 |
| `Error` | 错误信息（失败时） |
| `Reference` | Blob 引用（完成上传或读取时） |
| `Offset` | 当前读取偏移 |
| `EndOfBlob` | 是否已到达末尾 |

### 3.6 BlobReference

已存储 Blob 的引用。

```csharp
public sealed record BlobReference(
    string BlobId,
    long Length,
    string Sha256,
    string ContentType,
    string AccessScope,
    DateTimeOffset ExpiresAt);
```

## 4. Transport 层

### 4.1 IBlobChannel

传输抽象接口，解耦 Blob 逻辑与底层传输：

```csharp
public interface IBlobChannel
{
    ValueTask<Stream> OpenReadAsync(string blobId, CancellationToken cancellationToken = default);
    ValueTask<string> WriteAsync(Stream content, string contentType, CancellationToken cancellationToken = default);
}
```

| 方法 | 说明 |
| --- | --- |
| `OpenReadAsync` | 打开 Blob 的读取流 |
| `WriteAsync` | 写入内容并返回 Blob ID |

`OpenReadAsync` 返回的读取流与 §7 所说“不提供流式读取”并不矛盾：传输协议帧层本身只支持按 `Offset`/`MaxBytes` 分块读取（`BlobReadRequest`），`OpenReadAsync` 只是在其上封装的读取流适配器——每次 `Read` 内部发起一次分块读取帧并推进偏移，并非服务端主动流式推送。因此帧层能力仍是分块读取，`Stream` 只是客户端侧的便捷封装。

### 4.2 BlobFrameProtocol

命名管道传输的二进制帧编码，定义在 `Transport.NamedPipes/` 目录。

帧结构：

| 偏移 | 长度 | 说明 |
| --- | --- | --- |
| 0 | 4 | Magic: `MDB1` |
| 4 | 1 | 操作码（高比特 0x80 为响应标志） |
| 5 | 4 | 元数据长度 |
| 9 | N | 元数据（JSON，最大 64 KB） |
| 9+N | M | 数据载荷 |

操作码：

| 值 | 操作 |
| --- | --- |
| 1 | `Begin` |
| 2 | `Chunk` |
| 3 | `Complete` |
| 4 | `Read` |
| 5 | `Abort` |

响应帧的操作码在请求码基础上置位 `0x80`。

## 5. 限制

`RuntimeLimits` 中定义的 Blob 相关限制：

| 字段 | 默认值 | 说明 |
| --- | --- | --- |
| `MaxBlobBytes` | 1,073,741,824（1 GB） | 单个 Blob 最大字节数 |
| `MaxBlobBytesPerRun` | 4,294,967,296（4 GB） | 单个 Run 内所有 Blob 合计最大字节数 |
| `MaxInlineContentBytes` | 65,536（64 KB） | 内联内容上限，超过此值应使用 Blob 存储 |

当工具执行结果超过 `MaxInlineContentBytes` 时，Gateway 自动将结果写入 Blob 并在消息中引用 `BlobReference`，而非内联完整内容。

## 6. 维护

### 6.1 storage check

检查消息引用和 Blob 完整性：

```bash
madorin storage check [--verify-hashes]
```

| 选项 | 说明 |
| --- | --- |
| `--verify-hashes` | 流式读取 Blob 内容并验证 SHA-256 |

返回 `StorageCheckResult`，包含引用计数、Blob 文件数、孤儿数和诊断问题列表。

### 6.2 storage gc

回收未引用的孤儿 Blob：

```bash
madorin storage gc [--dry-run] [--older-than <days>] [--confirm]
```

| 选项 | 说明 |
| --- | --- |
| `--dry-run` | 只展示回收计划，不执行 |
| `--older-than <days>` | 只回收最后写入时间（`LastWriteTimeUtc`）超过指定天数的 Blob，默认 7 天 |
| `--confirm` | 确认执行回收，创建恢复点 |

`--dry-run` 和 `--confirm` 互斥。回收采用 move-not-delete 语义：不直接删除，而是将被移除的 Blob 移动到恢复点目录。

恢复点位于 `<dataDirectory>/backups/storage-gc-<timestamp>-<guid>/`，其中包含移入的 `blobs/` 子目录（存放 `<hash>.blob` 文件）以及记录本次回收内容的 `manifest.txt`。

### 6.3 共享语义

- Blob 跨 Session 共享，删除 Session 不会删除其引用的 Blob。
- 只有 `storage gc` 会移除 Blob，且受年龄保护。
- 恢复点中的 Blob 可以手动恢复或等待清理。

## 7. 边界

Blob 协议用于结构化二进制内容传输（工具结果、附件），不是通用文件传输机制。以下场景不使用 Blob：

- 消息正文使用 `messages/<sessionId>.jsonl` 存储。
- 技能文件使用技能物化目录。
- 对话历史投影使用 `messages/<sessionId>.compact.json`。

Blob 协议不提供：

- 流式读取（只支持分块读取）。
- 内容压缩或加密。
- 跨工作区传输。
- Blob 版本管理或变更通知。
