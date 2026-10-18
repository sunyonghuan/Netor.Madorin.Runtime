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
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<ProviderModel> Models => [new("chat", "Chat", ContextWindow: ChatWindow),
            new("summary", "Summary", ContextWindow: 32000)];
        public Task ValidateCapabilitiesAsync(ContentBlock[] input, CancellationToken ct = default) => Task.CompletedTask;
        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Started.TrySetResult();
            if (Pause)
            {
                await Release.Task.WaitAsync(ct);
            }
            yield return new TextDeltaProviderEvent(request.InvocationId, SummaryProjectionTests.ValidJson);
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }
    }
}
