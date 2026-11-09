using System.Text.Json.Serialization;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Providers.WorkerHost;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloFrame), "hello")]
[JsonDerivedType(typeof(HelloOkFrame), "hello.ok")]
[JsonDerivedType(typeof(HelloRejectedFrame), "hello.rejected")]
[JsonDerivedType(typeof(InvokeFrame), "invoke")]
[JsonDerivedType(typeof(EventFrame), "event")]
[JsonDerivedType(typeof(CancelFrame), "cancel")]
[JsonDerivedType(typeof(PingFrame), "ping")]
[JsonDerivedType(typeof(PongFrame), "pong")]
[JsonDerivedType(typeof(ShutdownFrame), "shutdown")]
[JsonDerivedType(typeof(ErrorFrame), "error")]
[JsonDerivedType(typeof(BlobReadFrame), "blob.read")]
[JsonDerivedType(typeof(BlobChunkFrame), "blob.chunk")]
[JsonDerivedType(typeof(BlobWriteFrame), "blob.write")]
[JsonDerivedType(typeof(BlobWriteAckFrame), "blob.write.ack")]
public abstract record ProviderWorkerFrame;

public sealed record HelloFrame(
    int IpcVersion,
    string Protocol,
    string Slug,
    string RuntimeVersion) : ProviderWorkerFrame;

public sealed record HelloOkFrame(
    int IpcVersion,
    string Slug,
    string Protocol,
    /// <summary>Worker 内真实适配器声明的能力；宿主侧投影/校验优先用它，避免配置缺 capabilities 时误判。</summary>
    ProviderCapabilities? Capabilities = null,
    IReadOnlyList<ProviderModel>? Models = null) : ProviderWorkerFrame;

public sealed record HelloRejectedFrame(
    string Code,
    string Message) : ProviderWorkerFrame;

public sealed record InvokeFrame(
    string InvocationId,
    ProviderConnectionConfig Connection,
    RuntimeProviderRequest Request,
    string? Generation = null,
    int MaximumBufferedBlobBytes = ProviderWorkerIpc.MaxBufferedBlobBytes,
    int HistoryMessageCount = 0) : ProviderWorkerFrame;

public sealed record BlobReadFrame(string InvocationId, string Generation, string TransferId,
    BlobReference Blob, long Offset, int Count) : ProviderWorkerFrame;

public sealed record BlobChunkFrame(string InvocationId, string Generation, string TransferId,
    long Offset, byte[] Data, bool EndOfBlob, string? Error = null) : ProviderWorkerFrame;

public sealed record BlobWriteFrame(string InvocationId, string Generation, string TransferId,
    string Operation, long Offset, byte[] Data, string? ContentType = null,
    long Length = 0, string? Sha256 = null) : ProviderWorkerFrame;

public sealed record BlobWriteAckFrame(string InvocationId, string Generation, string TransferId,
    string Operation, long Offset, BlobReference? Blob = null, string? Error = null) : ProviderWorkerFrame;

public sealed record EventFrame(
    string InvocationId,
    RuntimeProviderEvent Event,
    string? Generation = null) : ProviderWorkerFrame;

public sealed record CancelFrame(string InvocationId, string? Generation = null) : ProviderWorkerFrame;

public sealed record PingFrame(string Id) : ProviderWorkerFrame;

public sealed record PongFrame(string Id) : ProviderWorkerFrame;

public sealed record ShutdownFrame : ProviderWorkerFrame;

public sealed record ErrorFrame(
    string? InvocationId,
    string Code,
    string Message,
    string? Generation = null) : ProviderWorkerFrame;
