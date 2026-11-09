using System.Runtime.CompilerServices;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class CompactionServiceSafetyTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task LocalSelectionUpdate_IsRejectedDuringSlowCompaction()
    {
        var root = Path.Combine(Path.GetTempPath(), "local-compact-safety-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, ".madorin");
        var provider = new SafetyProvider { Pause = true };
        try
        {
            await using var runtime = await LocalRuntime.StartAsync(new LocalRuntimeOptions(root, data,
                Path.Combine(root, "logs"), Guid.NewGuid().ToString("N"), _ => provider)
                { MemoryUserHome = Path.Combine(root, "home") }, TestContext.CancellationToken);
            var (sessionId, selection) = await SeedAsync(data);
            var pending = runtime.CompactSessionAsync(sessionId,
                new SessionCompactionOptions(OperationId: "slow", RawTailSize: 0), TestContext.CancellationToken);
            await provider.Started.Task.WaitAsync(TestContext.CancellationToken);
            try
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.UpdateSessionSelectionAsync(
                    sessionId, 1, selection with { SelectionVersion = 2 }, TestContext.CancellationToken));
                StringAssert.Contains(error.Message, "session_busy");
            }
            finally
            {
                provider.Release.TrySetResult();
            }
            Assert.AreEqual("Applied", (await pending)!.Outcome);
            var updated = await runtime.UpdateSessionSelectionAsync(sessionId, 1,
                selection with { SelectionVersion = 2 }, TestContext.CancellationToken);
            Assert.AreEqual(2, updated.SelectionVersion);
        }
        finally
        {
            provider.Release.TrySetResult();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task SummaryBeyondChatBudget_FailsAndKeepsPreviousProjection()
    {
        var root = Path.Combine(Path.GetTempPath(), "summary-budget-safety-" + Guid.NewGuid().ToString("N"));
        var provider = new SafetyProvider();
        try
        {
            var (sessionId, _) = await SeedAsync(root);
            await using var service = new SessionCompactionService(root, Database(root), _ => provider);
            var first = await service.CompactAsync(sessionId,
                new SessionCompactionOptions(OperationId: "first", RawTailSize: 0), TestContext.CancellationToken);
            Assert.AreEqual("Applied", first!.Outcome);
            var store = new ConversationStore(root);
            await AppendAsync(store, sessionId, new string('x', 3000));
            await AppendAsync(store, sessionId, new string('y', 3000));
            provider.ChatWindow = 500;
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.CompactAsync(sessionId,
                new SessionCompactionOptions(OperationId: "small-window", RawTailSize: 1), TestContext.CancellationToken));
            StringAssert.Contains(error.Message, "context_budget_exhausted");
            var cache = await store.ReadCompactCacheAsync(sessionId, TestContext.CancellationToken);
            Assert.AreEqual(first.ProjectionRevision, cache!.ProjectionRevision);
            var status = await service.GetStatusAsync(sessionId, "small-window", TestContext.CancellationToken);
            Assert.AreEqual("Failed", status!.OperationState);
            Assert.AreEqual(first.ProjectionRevision, status.ProjectionRevision);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private async Task<(string SessionId, NextTurnSelection Selection)> SeedAsync(string data)
    {
        Directory.CreateDirectory(data);
        await using var connection = new SqliteConnection(Database(data));
        await connection.OpenAsync(TestContext.CancellationToken);
        await SqliteSchema.EnsureCreatedAsync(connection);
        var repository = new SqliteSessionRepository(connection);
        var sessionId = await repository.CreateSessionAsync(RuntimeMode.Expert, Guid.NewGuid().ToString("N"),
            TimeSpan.FromDays(1), TestContext.CancellationToken);
        var selection = new NextTurnSelection(1, RuntimeMode.Expert, new("p", "chat"),
            new ExpertModeOptions(new AgentRef("agent", "1", "Preserve facts."),
                contextPolicy: new ExpertContextPolicy(SummaryModelId: "summary")));
        await repository.TrySaveSessionSelectionAsync(sessionId, null, 1,
            JsonSerializer.Serialize(selection, RuntimeJsonContext.Default.NextTurnSelection), [], TestContext.CancellationToken);
        await AppendAsync(new ConversationStore(data), sessionId, new string('a', 5000));
        return (sessionId, selection);
    }

    /// <summary>
    /// 摘要 Provider 的协议错误会先回退为流式重试；仍失败时 runtime.jsonl 写出 CompactionFailed，
    /// 包含 Provider 错误码、ProviderDetails 和摘要请求诊断。
    /// </summary>
    [TestMethod]
    public async Task SummaryProviderFailure_WritesCompactionFailedDiagnosticEvent()
    {
        var root = Path.Combine(Path.GetTempPath(), "summary-failure-log-" + Guid.NewGuid().ToString("N"));
        var logDirectory = Path.Combine(root, "logs");
        var provider = new SafetyProvider
        {
            Failure = ProviderErrorFactory.Create(
                "p",
                new InvalidDataException("Provider stream ended before a terminal update."),
                isProtocolError: true,
                failureContext: new ProviderFailureContext(
                    ProviderFailureStages.StreamEndedWithoutFinishReason, 3, 1200))
        };
        try
        {
            var (sessionId, _) = await SeedAsync(root);
            await using (var log = RuntimeDiagnosticLog.Create(
                logDirectory, "test-instance", root, RuntimeDiagnosticLogLevel.Debug, TimeProvider.System))
            {
                await using var service = new SessionCompactionService(root, Database(root), _ => provider, log);
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompactAsync(sessionId,
                    new SessionCompactionOptions(OperationId: "failing", RawTailSize: 0), TestContext.CancellationToken));
                StringAssert.Contains(error.Message, "ProviderProtocolError");
            }

            Assert.AreEqual("True,False", string.Join(',', provider.NonStreamingRequests));
            var logFile = Assert.ContainsSingle(Directory.GetFiles(logDirectory, "runtime.jsonl", SearchOption.AllDirectories));
            var failed = File.ReadAllLines(logFile)
                .Select(static line => JsonDocument.Parse(line).RootElement)
                .Single(static entry => entry.GetProperty("event").GetString() == "CompactionFailed");
            Assert.AreEqual("Error", failed.GetProperty("level").GetString());
            Assert.AreEqual(sessionId, failed.GetProperty("sessionId").GetString());
            Assert.AreEqual(RuntimeErrorCodes.ProviderProtocolError, failed.GetProperty("errorCode").GetString());
            Assert.AreEqual("failing", failed.GetProperty("operationId").GetString());
            var details = failed.GetProperty("providerDetails");
            Assert.AreEqual(typeof(InvalidDataException).FullName, details.GetProperty("exceptionTypes")[0].GetString());
            Assert.AreEqual(ProviderFailureStages.StreamEndedWithoutFinishReason, details.GetProperty("stage").GetString());
            var summaryRequest = failed.GetProperty("summaryRequest");
            Assert.AreEqual("streaming", summaryRequest.GetProperty("responseMode").GetString());
            Assert.AreEqual(2, summaryRequest.GetProperty("requestCount").GetInt32());
            Assert.AreEqual("summary", summaryRequest.GetProperty("modelId").GetString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private Task<ConversationRecordV1> AppendAsync(ConversationStore store, string sessionId, string text) =>
        store.AppendMessageAsync(sessionId, new ConversationMessageDraft("inv", "agent", "user",
            JsonSerializer.SerializeToElement<ContentBlock[]>([new TextContentBlock(text)], RuntimeJsonContext.Default.ContentBlockArray),
            DateTimeOffset.UtcNow), TestContext.CancellationToken);

    private static string Database(string data) => new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(data, "state.db") }.ToString();

    private sealed class SafetyProvider : IRuntimeProviderAdapter
    {
        public string ProviderId => "p";
        public ProviderCapabilities Capabilities { get; } = new();
        public int ChatWindow { get; set; } = 32000;
        public bool Pause { get; init; }
        /// <summary>设置后每次请求都返回该失败事件。</summary>
        public RuntimeError? Failure { get; init; }
        /// <summary>按顺序记录每次请求是否为非流式。</summary>
        public List<bool> NonStreamingRequests { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<ProviderModel> Models => [new("chat", "Chat", ContextWindow: ChatWindow),
            new("summary", "Summary", ContextWindow: 32000)];
        public Task ValidateCapabilitiesAsync(ContentBlock[] input, CancellationToken ct = default) => Task.CompletedTask;
        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Started.TrySetResult();
            NonStreamingRequests.Add(request.UseNonStreamingResponse);
            if (Pause)
            {
                await Release.Task.WaitAsync(ct);
            }

            if (Failure is not null)
            {
                yield return new InvocationFailedProviderEvent(request.InvocationId, Failure);
                yield break;
            }

            yield return new TextDeltaProviderEvent(request.InvocationId, SummaryProjectionTests.ValidJson);
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }
    }
}
