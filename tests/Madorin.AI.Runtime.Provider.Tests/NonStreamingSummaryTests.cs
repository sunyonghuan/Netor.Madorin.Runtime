using System.Runtime.CompilerServices;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Modes.Expert;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.WorkerHost;

namespace Madorin.AI.Runtime.Provider.Tests;

/// <summary>摘要请求的非流式调用、流式回退与失败诊断。</summary>
[TestClass]
public sealed class NonStreamingSummaryTests
{
    private readonly TestContext _testContext;

    public NonStreamingSummaryTests(TestContext testContext)
    {
        _testContext = testContext;
    }

    /// <summary>三类协议的适配器在非流式模式下发出 stream=false 请求，并把完整响应映射为文本与完成事件。</summary>
    [TestMethod]
    [DataRow(ProviderAdapterKind.OpenAI)]
    [DataRow(ProviderAdapterKind.Anthropic)]
    [DataRow(ProviderAdapterKind.OpenAICompatible)]
    public async Task CompleteStreamingAsync_NonStreamingRequest_ReturnsCompleteResponse(ProviderAdapterKind kind)
    {
        await using var server = await FakeProviderServer.StartAsync(
            _ => new FakeProviderResponse(200, "application/json", [new FakeProviderChunk(NonStreamingText(kind))]),
            _testContext.CancellationToken);
        using var fixture = ProviderAdapterTestFactory.Create(kind, server.BaseAddress);
        var request = ProviderAdapterTestFactory.CreateRequest() with { UseNonStreamingResponse = true };

        var events = await ProviderAdapterTestFactory.CollectAsync(
            fixture.Adapter,
            request,
            _testContext.CancellationToken);

        Assert.IsEmpty(
            events.OfType<InvocationFailedProviderEvent>(),
            string.Join(" | ", events.OfType<InvocationFailedProviderEvent>()
                .Select(static e => $"{e.Error.Code}: {e.Error.Message} {e.Error.ProviderDetails?.GetRawText()}")));
        Assert.AreEqual("Hello", string.Concat(events.OfType<TextDeltaProviderEvent>().Select(static e => e.Delta)));
        Assert.ContainsSingle(events.OfType<InvocationCompletedProviderEvent>());
        var captured = Assert.ContainsSingle(server.Requests);
        using var body = JsonDocument.Parse(captured.Body);
        Assert.IsFalse(
            body.RootElement.TryGetProperty("stream", out var stream) && stream.ValueKind == JsonValueKind.True,
            "非流式请求不应携带 stream=true。");
    }

    /// <summary>非流式标记随请求经 Worker 进程帧序列化后保留，Worker 侧适配器才能按非流式调用。</summary>
    [TestMethod]
    public async Task WorkerInvoke_RoundTrip_PreservesNonStreamingFlag()
    {
        var request = ProviderAdapterTestFactory.CreateRequest() with { UseNonStreamingResponse = true };
        await using var stream = new MemoryStream();
        await ProviderWorkerFrameCodec.WriteAsync(stream, new InvokeFrame(
            request.InvocationId,
            new ProviderConnectionConfig("test", "OpenAI Compatible", "http://localhost", "test-key"),
            request), _testContext.CancellationToken);
        stream.Position = 0;

        var read = await ProviderWorkerFrameCodec.ReadAsync(stream, _testContext.CancellationToken);
        var restored = Assert.IsInstanceOfType<InvokeFrame>(read.Frame);
        Assert.IsTrue(restored.Request.UseNonStreamingResponse);
    }

    /// <summary>服务端以 400 拒绝非流式请求时，摘要器改用流式重发一次并成功。</summary>
    [TestMethod]
    public async Task Summarizer_NonStreamingRejected_FallsBackToStreaming()
    {
        var provider = new ScriptedSummaryProvider(request => request.UseNonStreamingResponse
            ? ProviderErrorFactory.Create("p", new InvalidOperationException("stream required"), statusCode: 400)
            : null);

        var summary = await StrictConversationSummarizer.GenerateAsync(
            provider, "p", "summary", History(), ct: _testContext.CancellationToken);

        Assert.IsTrue(StrictConversationSummarizer.IsValidatedSummary(summary));
        Assert.AreEqual(
            "True,False",
            string.Join(',', provider.Requests.Select(static r => r.UseNonStreamingResponse)));
    }

    /// <summary>5xx 不回退，异常携带 Provider 原始错误和本次摘要请求的诊断。</summary>
    [TestMethod]
    public async Task Summarizer_ServerError_ThrowsWithRequestDiagnostics()
    {
        var provider = new ScriptedSummaryProvider(_ =>
            ProviderErrorFactory.Create("p", new InvalidOperationException("upstream"), statusCode: 503));

        var error = await Assert.ThrowsExactlyAsync<SummaryProviderFailedException>(() =>
            StrictConversationSummarizer.GenerateAsync(
                provider, "p", "summary", History(), ct: _testContext.CancellationToken));

        Assert.ContainsSingle(provider.Requests);
        StringAssert.StartsWith(error.Message, "Summary provider failed (");
        Assert.AreEqual("non_streaming", error.Request.ResponseMode);
        Assert.AreEqual("summary", error.Request.ModelId);
        Assert.AreEqual(1, error.Request.BatchIndex);
        Assert.AreEqual(1, error.Request.BatchCount);
        Assert.AreEqual(1, error.Request.RequestCount);
        Assert.AreEqual(32000, error.Request.ContextWindow);
        var details = error.ProviderError.ProviderDetails!.Value;
        Assert.AreEqual(503, details.GetProperty("statusCode").GetInt32());
        Assert.AreEqual(
            typeof(InvalidOperationException).FullName,
            details.GetProperty("exceptionTypes")[0].GetString());
    }

    private static RuntimeProviderMessage[] History() =>
    [
        new(RuntimeProviderRoles.User, [new TextContentBlock("history " + new string('a', 2000))]),
        new(RuntimeProviderRoles.Assistant, [new TextContentBlock("answer " + new string('b', 2000))])
    ];

    private static string NonStreamingText(ProviderAdapterKind kind) => kind switch
    {
        ProviderAdapterKind.OpenAI =>
            "{\"id\":\"resp-1\",\"object\":\"response\",\"created_at\":1,\"status\":\"completed\",\"error\":null,\"incomplete_details\":null,\"instructions\":null,\"max_output_tokens\":128,\"model\":\"test-model\",\"output\":[{\"type\":\"message\",\"id\":\"msg-1\",\"status\":\"completed\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"Hello\",\"annotations\":[]}]}],\"parallel_tool_calls\":true,\"previous_response_id\":null,\"reasoning\":{\"effort\":null,\"summary\":null},\"store\":false,\"temperature\":0.25,\"text\":{\"format\":{\"type\":\"text\"}},\"tool_choice\":\"auto\",\"tools\":[],\"top_p\":1.0,\"truncation\":\"disabled\",\"usage\":{\"input_tokens\":5,\"input_tokens_details\":{\"cached_tokens\":0},\"output_tokens\":2,\"output_tokens_details\":{\"reasoning_tokens\":0},\"total_tokens\":7},\"metadata\":{}}",
        ProviderAdapterKind.Anthropic =>
            "{\"id\":\"msg-1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"test-model\",\"content\":[{\"type\":\"text\",\"text\":\"Hello\"}],\"stop_reason\":\"end_turn\",\"stop_sequence\":null,\"usage\":{\"input_tokens\":5,\"output_tokens\":2}}",
        ProviderAdapterKind.OpenAICompatible =>
            "{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"test-model\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"Hello\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":2,\"total_tokens\":7}}",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    /// <summary>按脚本返回失败或有效摘要，并记录收到的请求。</summary>
    private sealed class ScriptedSummaryProvider(Func<RuntimeProviderRequest, RuntimeError?> failure) : IRuntimeProviderAdapter
    {
        public List<RuntimeProviderRequest> Requests { get; } = [];
        public string ProviderId => "p";
        public ProviderCapabilities Capabilities { get; } = new();
        public IReadOnlyList<ProviderModel> Models { get; } = [new("summary", "Summary", ContextWindow: 32000)];
        public Task ValidateCapabilitiesAsync(ContentBlock[] input, CancellationToken ct = default) => Task.CompletedTask;

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            Requests.Add(request);
            if (failure(request) is { } error)
            {
                yield return new InvocationFailedProviderEvent(request.InvocationId, error);
                yield break;
            }

            yield return new TextDeltaProviderEvent(request.InvocationId, SummaryProjectionTests.ValidJson);
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }
    }
}
