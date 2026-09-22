using System.Text.Json.Serialization;
using Madorin.AI.Runtime.Providers.Abstractions;

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
    ProviderCapabilities? Capabilities = null) : ProviderWorkerFrame;

public sealed record HelloRejectedFrame(
    string Code,
    string Message) : ProviderWorkerFrame;

public sealed record InvokeFrame(
    string InvocationId,
    ProviderConnectionConfig Connection,
    RuntimeProviderRequest Request) : ProviderWorkerFrame;

public sealed record EventFrame(
    string InvocationId,
    RuntimeProviderEvent Event) : ProviderWorkerFrame;

public sealed record CancelFrame(string InvocationId) : ProviderWorkerFrame;

public sealed record PingFrame(string Id) : ProviderWorkerFrame;

public sealed record PongFrame(string Id) : ProviderWorkerFrame;

public sealed record ShutdownFrame : ProviderWorkerFrame;

public sealed record ErrorFrame(
    string? InvocationId,
    string Code,
    string Message) : ProviderWorkerFrame;
