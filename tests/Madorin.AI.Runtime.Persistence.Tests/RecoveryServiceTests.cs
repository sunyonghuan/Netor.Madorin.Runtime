using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Server;
using Madorin.AI.Runtime.Services;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Persistence.Tests;

[TestClass]
public sealed class RecoveryServiceTests
{
    private static readonly TimeSpan KeyRetention = TimeSpan.FromDays(1);

    private readonly TestContext _testContext;

    public RecoveryServiceTests(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestMethod]
    public async Task RecoverOnStartupAsync_WithRunningRun_MarksRunInterrupted()
    {
        var ct = _testContext.CancellationToken;
        await using var connection = await CreateDatabaseAsync(ct);
        var (repository, _, runId) = await CreateRunAsync(connection, RuntimeMode.Expert, ct);
        await repository.TransitionRunStatusAsync(
            runId,
            RunStatus.Accepted,
            RunStatus.Preparing,
            ct);
        await repository.TransitionRunStatusAsync(
            runId,
            RunStatus.Preparing,
            RunStatus.Running,
            ct);
        var service = new RecoveryService(repository);

        await service.RecoverOnStartupAsync(ct);

        Assert.AreEqual(
            RunStatus.Interrupted,
            await repository.GetRunStatusAsync(runId, ct));
    }

    [TestMethod]
    public async Task ResumeSessionAsync_WithExistingSession_ReturnsLatestRunState()
    {
        var ct = _testContext.CancellationToken;
        await using var connection = await CreateDatabaseAsync(ct);
        var (repository, sessionId, runId) = await CreateRunAsync(
            connection,
            RuntimeMode.Expert,
            ct);
        await repository.TransitionRunStatusAsync(
            runId,
            RunStatus.Accepted,
            RunStatus.Preparing,
            ct);
        var service = new RecoveryService(repository);

        var resumeInfo = await service.ResumeSessionAsync(sessionId, ct);

        Assert.IsNotNull(resumeInfo);
        Assert.AreEqual(sessionId, resumeInfo.SessionId);
        Assert.AreEqual(RuntimeMode.Expert, resumeInfo.Mode);
        Assert.AreEqual(runId, resumeInfo.LastRunId);
        Assert.AreEqual(RunStatus.Preparing.ToString(), resumeInfo.LastStatus);
    }

    private static async Task<(SqliteSessionRepository, string, string)> CreateRunAsync(
        SqliteConnection connection,
        RuntimeMode mode,
        CancellationToken ct)
    {
        var repository = new SqliteSessionRepository(connection);
        var sessionId = Guid.NewGuid().ToString("N");
        var runId = Guid.NewGuid().ToString("N");
        var selection = CreateSelection(mode);
        var selectionJson = System.Text.Json.JsonSerializer.Serialize(
            selection,
            Contracts.Serialization.RuntimeJsonContext.Default.NextTurnSelection);
        var snapshot = RuntimeRunMetadata.CreateAgentSnapshots(selection);
        await repository.CreateSessionAsync(
            mode,
            sessionId,
            KeyRetention,
            ct);
        await repository.TrySaveSessionSelectionAsync(
            sessionId,
            expectedSelectionVersion: null,
            selectionVersion: 1,
            selectionJson,
            snapshot,
            ct);
        await repository.CreateRunAsync(
            sessionId,
            runId,
            KeyRetention,
            ct);
        return (repository, sessionId, runId);
    }

    private static NextTurnSelection CreateSelection(RuntimeMode mode)
    {
        var agent = new AgentRef("test-agent", "v1", "Test system prompt");
        ModeOptions options = mode switch
        {
            RuntimeMode.Expert => new ExpertModeOptions(agent),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };
        return new NextTurnSelection(
            1,
            mode,
            new DefaultSelection("test-provider", "test-model"),
            options);
    }

    private static async Task<SqliteConnection> CreateDatabaseAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        await SqliteSchema.EnsureCreatedAsync(connection, ct);
        return connection;
    }
}
