using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server;
using Madorin.AI.Runtime.Transport.NamedPipes;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

/// <summary>
/// 造一个 host.* 工具（与插件工具同路径：catalog → consent → reverse tool.call → 宿主执行），
/// 验证调用链路通，不依赖真实插件进程。
/// </summary>
[TestClass]
public sealed class HostFabricatedToolCallChainTests
{
    // 形态对齐插件映射后的 Runtime ToolId（host.provider..name..version 可读结构亦可，测试用短 id）
    private const string FabricatedToolId = "host.scriptrunner.echo";
    private const string FabricatedCallName = "script_echo";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task FabricatedHostTool_ConsentThenToolCall_RoundTripsThroughReverseRpc()
    {
        var workspace = CreateTempDir();
        var instanceId = Guid.NewGuid().ToString("N");
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var provider = new ForcedHostToolProvider();
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        try
        {
            await using var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = instanceId,
                    PipePrefix = $"madorin.host-fabricated.{Guid.NewGuid():N}",
                    HandshakeSecret = secret,
                    MemoryUserHome = Path.Combine(workspace, "home"),
                    ProviderResolver = _ => provider
                },
                TestContext.CancellationToken);
            var serverTask = server.RunAsync(shutdown.Token);

            await using var client = new RuntimeClient(
                new RuntimeClientOptions(
                    server.PipeName,
                    Guid.NewGuid().ToString("N"),
                    ExpectedRuntimeInstanceId: instanceId,
                    HandshakeSecret: secret,
                    Capabilities: new RuntimeCapabilities(
                        ReverseRpc: true,
                        ToolCatalog: true,
                        ToolPermissions: true)));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await client.ConnectAsync(timeout.Token);

            var host = new FabricatedToolHost(workspace);
            client.ControlPeer.SetRequestHandler(host.HandleAsync);
            await PublishFabricatedToolAsync(client, timeout.Token);

            var runId = await client.StartNewSessionRunAsync(
                new NewSessionRunRequest(
                    $"sess-{Guid.NewGuid():N}",
                    $"turn-{Guid.NewGuid():N}",
                    RuntimeMode.Expert,
                    new NextTurnSelection(
                        1,
                        RuntimeMode.Expert,
                        new DefaultSelection("fab-provider", "fab-model"),
                        new ExpertModeOptions(
                            new AgentRef("agent-1", "v1", "Call the fabricated host tool."))),
                    [new TextContentBlock("Echo hello via host tool.")]),
                timeout.Token);

            var terminal = await ReadTerminalAsync(client, runId, timeout.Token);
            if (terminal.MessageType == MessageTypes.RunFailed)
            {
                var failed = JsonSerializer.Deserialize(
                    terminal.Payload,
                    RuntimeJsonContext.Default.RunFailedEvent);
                Assert.Fail($"Run failed: {failed?.Error.Code}: {failed?.Error.Message}");
            }

            Assert.AreEqual(MessageTypes.RunCompleted, terminal.MessageType);
            Assert.IsGreaterThanOrEqualTo(1, host.PermissionCount, "应走过宿主 permission 同意。");
            Assert.IsGreaterThanOrEqualTo(1, host.ToolCallCount, "应走过 reverse tool.call 执行。");
            Assert.AreEqual(FabricatedToolId, host.LastExecutedToolId);
            Assert.IsTrue(host.LastArgumentsJson.Contains("hello", StringComparison.OrdinalIgnoreCase));
            Assert.IsGreaterThanOrEqualTo(2, provider.CallCount, "工具结果回传后 Provider 应再被调用一轮。");
            var run = await client.QueryRunAsync(runId, timeout.Token);
            StringAssert.Contains(run.TerminalText ?? string.Empty, "echo-ok");

            shutdown.Cancel();
            await serverTask;
        }
        finally
        {
            shutdown.Cancel();
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    private static async Task PublishFabricatedToolAsync(RuntimeClient client, CancellationToken ct)
    {
        var request = new ToolCatalogReplaceRequest(
            "fabricated-v1",
            [
                new ToolCatalogItem(
                    FabricatedToolId,
                    FabricatedCallName,
                    "Fabricated host script-style tool (same path as plugin tools).",
                    Parse("""{"type":"object","properties":{"message":{"type":"string"}},"required":["message"]}"""),
                    Parse("""{"type":"object","properties":{"ok":{"type":"boolean"},"echo":{"type":"string"}}}"""),
                    // 与插件发布一致用 Host 目标即可；能力列表空时 Host Grant 不需要 AllowedExecutables
                    ToolRiskLevel.Write,
                    15,
                    ["fabricated", "script"],
                    [],
                    ToolExecutionTarget.Host,
                    RequiresApproval: false,
                    IsIdempotent: false)
            ]);
        var response = await client.ControlPeer.SendRequestAsync(
            MessageTypes.ToolCatalogReplace,
            JsonSerializer.SerializeToElement(
                request,
                RuntimeJsonContext.Default.ToolCatalogReplaceRequest),
            TimeSpan.FromSeconds(5),
            ct);
        Assert.IsNull(response.Error, response.Error?.Message);
    }

    private static async Task<RuntimeEventEnvelope> ReadTerminalAsync(
        RuntimeClient client,
        string runId,
        CancellationToken ct)
    {
        await foreach (var envelope in client.ReadEventsAsync(0, ct))
        {
            if (envelope.RunId == runId
                && envelope.MessageType is MessageTypes.RunCompleted
                    or MessageTypes.RunFailed
                    or MessageTypes.RunCancelled)
            {
                return envelope;
            }
        }

        throw new InvalidOperationException($"Run '{runId}' had no terminal event.");
    }

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "madorin-host-fabricated",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>强制调用造出来的 host 工具，再读 tool result 收尾。</summary>
    private sealed class ForcedHostToolProvider : IRuntimeProviderAdapter
    {
        public string ProviderId => "fab-provider";

        public ProviderCapabilities Capabilities { get; } = new(
            Streaming: true,
            ToolCalling: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("fab-model", "Fab Model", ContextWindow: 8_192)];

        public int CallCount { get; private set; }

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            CallCount++;
            await Task.Yield();
            ct.ThrowIfCancellationRequested();

            if (CallCount == 1)
            {
                Assert.IsNotNull(request.Tools);
                Assert.IsTrue(
                    request.Tools.Any(t => t.ToolId == FabricatedToolId),
                    "造出来的 host 工具必须出现在本轮 Provider Tools 中。");
                yield return new ToolCallCompleteProviderEvent(
                    request.InvocationId,
                    "fab-call-1",
                    FabricatedToolId,
                    FabricatedCallName,
                    """{"message":"hello-from-fabricated-host-tool"}""");
                yield return new InvocationCompletedProviderEvent(
                    request.InvocationId,
                    "tool_calls");
                yield break;
            }

            var results = request.Messages
                .Where(m => m.Role == RuntimeProviderRoles.Tool)
                .SelectMany(m => m.Content)
                .OfType<ToolResultContentBlock>()
                .ToArray();
            Assert.IsGreaterThanOrEqualTo(1, results.Length);
            var text = string.Join(
                ' ',
                results.SelectMany(r => r.Content.OfType<TextContentBlock>().Select(t => t.Text)));
            Assert.IsTrue(
                text.Contains("echo-ok", StringComparison.Ordinal)
                || text.Contains("hello-from-fabricated", StringComparison.Ordinal),
                $"工具结果未回到 Provider：{text}");

            yield return new TextDeltaProviderEvent(request.InvocationId, "echo-ok");
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>
    /// 假宿主：permission 自动同意（Host 风格 Grant）+ tool.call 直接回脚本结果。
    /// 插件路径只是在 HandleToolCall 里多一次进程通讯，协议层相同。
    /// </summary>
    private sealed class FabricatedToolHost(string workspace)
    {
        private int _permissionCount;
        private int _toolCallCount;

        public int PermissionCount => Volatile.Read(ref _permissionCount);

        public int ToolCallCount => Volatile.Read(ref _toolCallCount);

        public string? LastExecutedToolId { get; private set; }

        public string LastArgumentsJson { get; private set; } = string.Empty;

        public ValueTask<JsonRpcResponse> HandleAsync(JsonRpcRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return request.Method switch
            {
                MessageTypes.ToolPermissionRequest => ValueTask.FromResult(HandlePermission(request)),
                MessageTypes.ApprovalRequest => ValueTask.FromResult(HandleApproval(request)),
                MessageTypes.ToolCallRequest => ValueTask.FromResult(HandleToolCall(request)),
                MessageTypes.ToolResultQuery => ValueTask.FromResult(HandleResultQuery(request)),
                MessageTypes.ToolCallCancel => ValueTask.FromResult(
                    Ok(request, true, RuntimeJsonContext.Default.Boolean)),
                _ => ValueTask.FromResult(new JsonRpcResponse(
                    "2.0",
                    request.Id,
                    Error: new JsonRpcError(-32601, $"Unknown method '{request.Method}'.")))
            };
        }

        private JsonRpcResponse HandlePermission(JsonRpcRequest request)
        {
            Interlocked.Increment(ref _permissionCount);
            var permission = Req(request, RuntimeJsonContext.Default.ToolPermissionRequest);
            var grantId = $"fab-grant-{permission.CallId}";
            // 故意模拟 HostToolGrantFactory：有 ToolId，AllowedExecutables 可为空（无 process 能力时足够）
            var grant = new ToolGrant(
                grantId,
                permission.RunId ?? "run",
                workspace,
                [workspace],
                [workspace],
                AllowOverwrite: false,
                AllowMove: false,
                AllowDelete: false,
                AllowedExecutables: [],
                AllowPowerShell: false,
                new NetworkPolicy(),
                DateTimeOffset.UtcNow.AddHours(1),
                AllowDelegation: false,
                DelegatedAgentIds: [],
                AllowedToolIds: [permission.ToolId],
                AllowedCallIds: [permission.CallId],
                MaximumRisk: ToolRiskLevel.Write,
                AgentId: permission.AgentId,
                RootGrantId: grantId,
                ApprovalRequestId: permission.ApprovalRequestId);
            return Ok(
                request,
                new ToolPermissionResponse(
                    permission.CorrelationId ?? permission.ApprovalRequestId,
                    permission.CallId,
                    ToolAuthorizationDecision.Granted,
                    grant,
                    "fabricated host auto-grant"),
                RuntimeJsonContext.Default.ToolPermissionResponse);
        }

        private static JsonRpcResponse HandleApproval(JsonRpcRequest request)
        {
            var approval = Req(request, RuntimeJsonContext.Default.ApprovalRequest);
            return Ok(
                request,
                new ApprovalResponse(
                    approval.CorrelationId,
                    approval.ApprovalRequestId,
                    ToolAuthorizationDecision.Granted,
                    Grant: null,
                    Reason: "not used"),
                RuntimeJsonContext.Default.ApprovalResponse);
        }

        private JsonRpcResponse HandleToolCall(JsonRpcRequest request)
        {
            Interlocked.Increment(ref _toolCallCount);
            var call = Req(request, RuntimeJsonContext.Default.ToolCallRequest);
            LastExecutedToolId = call.ToolId;
            LastArgumentsJson = call.Arguments.GetRawText();
            Assert.AreEqual(FabricatedToolId, call.ToolId);
            Assert.IsFalse(string.IsNullOrWhiteSpace(call.GrantId), "tool.call 必须带 GrantId。");

            var message = call.Arguments.TryGetProperty("message", out var m)
                ? m.GetString() ?? ""
                : "";
            var result = Parse(
                $$"""{"ok":true,"echo":"{{message}}","marker":"echo-ok"}""");
            return Ok(
                request,
                new ToolCallResponse(
                    call.CorrelationId,
                    call.CallId,
                    ToolCallStatus.Succeeded,
                    result),
                RuntimeJsonContext.Default.ToolCallResponse);
        }

        private static JsonRpcResponse HandleResultQuery(JsonRpcRequest request)
        {
            var query = Req(request, RuntimeJsonContext.Default.ToolResultQueryRequest);
            return Ok(
                request,
                new ToolResultQueryResponse(
                    query.CorrelationId,
                    query.CallId,
                    ToolCallStatus.Unknown),
                RuntimeJsonContext.Default.ToolResultQueryResponse);
        }

        private static T Req<T>(
            JsonRpcRequest request,
            System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
            request.Params is { } p
                ? JsonSerializer.Deserialize(p, typeInfo)
                    ?? throw new InvalidDataException("empty params")
                : throw new InvalidDataException("missing params");

        private static JsonRpcResponse Ok<T>(
            JsonRpcRequest request,
            T body,
            System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
            new("2.0", request.Id, JsonSerializer.SerializeToElement(body, typeInfo));
    }
}
