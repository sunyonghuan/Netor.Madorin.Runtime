using System.Diagnostics;
using System.Globalization;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Persistence.Tests;

[TestClass]
public sealed class SqliteSessionRepositoryTests
{
    private static readonly TimeSpan KeyRetention = TimeSpan.FromDays(1);
    private static readonly string[] ResumeSentToolCallIds = ["call-step-a"];

    public TestContext TestContext { get; set; }

    [TestMethod]
    public async Task CreateSessionAsync_WithSameIdempotencyKey_ReturnsSameSessionId()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var repository = new SqliteSessionRepository(connection);

        var first = await repository.CreateSessionAsync(
            RuntimeMode.Expert,
            "session-key",
            KeyRetention,
            TestContext.CancellationToken);
        var second = await repository.CreateSessionAsync(
            RuntimeMode.Expert,
            "session-key",
            KeyRetention,
            TestContext.CancellationToken);

        Assert.AreEqual(first, second);
    }

    [TestMethod]
    public async Task CreateRunAsync_WithSameIdempotencyKey_ReturnsSameRunId()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var repository = new SqliteSessionRepository(connection);
        var sessionId = await repository.CreateSessionAsync(
            RuntimeMode.Expert,
            "session-key",
            KeyRetention,
            TestContext.CancellationToken);

        var first = await repository.CreateRunAsync(
            sessionId,
            "run-key",
            KeyRetention,
            TestContext.CancellationToken);
        var second = await repository.CreateRunAsync(
            sessionId,
            "run-key",
            KeyRetention,
            TestContext.CancellationToken);

        Assert.AreEqual(first, second);
    }

    [TestMethod]
    public async Task CreateSessionAsync_SameKeyWithDifferentRequestHash_ThrowsConflict()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var repository = new SqliteSessionRepository(connection);
        await repository.CreateSessionAsync(
            RuntimeMode.Expert,
            "session-key",
            KeyRetention,
            "hash-1",
            TestContext.CancellationToken);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await repository.CreateSessionAsync(
                RuntimeMode.Expert,
                "session-key",
                KeyRetention,
                "hash-2",
                TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task CreateRunAsync_SameKeyWithDifferentRequestHash_ThrowsConflict()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var repository = new SqliteSessionRepository(connection);
        var sessionId = await repository.CreateSessionAsync(
            RuntimeMode.Expert,
            "session-key",
            KeyRetention,
            TestContext.CancellationToken);
        await repository.CreateRunAsync(
            sessionId,
            "run-key",
            KeyRetention,
            "hash-1",
            TestContext.CancellationToken);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await repository.CreateRunAsync(
                sessionId,
                "run-key",
                KeyRetention,
                "hash-2",
                TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task TransitionRunStatusAsync_WithLegalPath_PersistsTargetStatus()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var (repository, runId) = await CreateRunAsync(connection, TestContext.CancellationToken);

        await repository.TransitionRunStatusAsync(
            runId,
            RunStatus.Accepted,
            RunStatus.Preparing,
            TestContext.CancellationToken);
        await repository.TransitionRunStatusAsync(
            runId,
            RunStatus.Preparing,
            RunStatus.Running,
            TestContext.CancellationToken);

        Assert.AreEqual(
            RunStatus.Running,
            await repository.GetRunStatusAsync(runId, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task TransitionRunStatusAsync_WithIllegalPath_ThrowsInvalidOperationException()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var (repository, runId) = await CreateRunAsync(connection, TestContext.CancellationToken);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await repository.TransitionRunStatusAsync(
                runId,
                RunStatus.Accepted,
                RunStatus.Completed,
                TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task TransitionRunToTerminalAsync_PersistsTextWithTerminalStateAtomically()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var (repository, runId) = await CreateRunAsync(
            connection,
            TestContext.CancellationToken);
        await repository.TransitionRunStatusAsync(
            runId,
            RunStatus.Accepted,
            RunStatus.Preparing,
            TestContext.CancellationToken);
        await repository.TransitionRunStatusAsync(
            runId,
            RunStatus.Preparing,
            RunStatus.Running,
            TestContext.CancellationToken);
        await repository.TransitionRunStatusAsync(
            runId,
            RunStatus.Running,
            RunStatus.Persisting,
            TestContext.CancellationToken);

        var active = await repository.GetRunSnapshotAsync(
            runId,
            TestContext.CancellationToken);
        Assert.IsNotNull(active);
        Assert.AreEqual(RunStatus.Persisting, active.Status);
        Assert.IsNull(active.TerminalText);

        await repository.TransitionRunToTerminalAsync(
            runId,
            RunStatus.Persisting,
            RunStatus.Completed,
            "  最终答案\nline 2  ",
            TestContext.CancellationToken);

        var completed = await repository.GetRunSnapshotAsync(
            runId,
            TestContext.CancellationToken);
        Assert.IsNotNull(completed);
        Assert.AreEqual(RunStatus.Completed, completed.Status);
        Assert.AreEqual("  最终答案\nline 2  ", completed.TerminalText);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => repository.TransitionRunToTerminalAsync(
                runId,
                RunStatus.Persisting,
                RunStatus.Failed,
                "replacement",
                TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task TryCommitTerminalRunAsync_FailureBeforeTransaction_LeavesRunAndOutboxUnchanged()
    {
        await AssertTerminalCommitFailureLeavesNoPartialStateAsync(
            SqliteSessionRepositoryFailurePoint.BeforeTerminalTransaction);
    }

    [TestMethod]
    public async Task TryCommitTerminalRunAsync_FailureBeforeCommit_LeavesRunAndOutboxUnchanged()
    {
        await AssertTerminalCommitFailureLeavesNoPartialStateAsync(
            SqliteSessionRepositoryFailurePoint.BeforeTerminalCommit);
    }

    private async Task AssertTerminalCommitFailureLeavesNoPartialStateAsync(
        SqliteSessionRepositoryFailurePoint failurePoint)
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var (setupRepository, runId) = await CreateRunAsync(
            connection,
            TestContext.CancellationToken);
        await setupRepository.TransitionRunStatusAsync(
            runId,
            RunStatus.Accepted,
            RunStatus.Preparing,
            TestContext.CancellationToken);
        await setupRepository.TransitionRunStatusAsync(
            runId,
            RunStatus.Preparing,
            RunStatus.Running,
            TestContext.CancellationToken);
        await setupRepository.TransitionRunStatusAsync(
            runId,
            RunStatus.Running,
            RunStatus.Persisting,
            TestContext.CancellationToken);

        var repository = new SqliteSessionRepository(
            connection,
            new SqliteSessionRepositoryOptions
            {
                FailureInjector = (point, _) => point == failurePoint
                    ? ValueTask.FromException(new InvalidOperationException("injected terminal failure"))
                    : ValueTask.CompletedTask
            });
        var payload = "{\"runId\":\"" + runId + "\",\"sessionId\":\"session\"}";
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => repository.TryCommitTerminalRunAsync(
                runId,
                RunStatus.Completed,
                "answer",
                null,
                MessageTypes.RunCompleted,
                payload,
                TestContext.CancellationToken));

        var snapshot = await setupRepository.GetRunSnapshotAsync(
            runId,
            TestContext.CancellationToken);
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(RunStatus.Persisting, snapshot.Status);
        Assert.IsNull(snapshot.TerminalText);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM event_outbox WHERE run_id = $runId;";
        command.Parameters.AddWithValue("$runId", runId);
        Assert.AreEqual(0L, Convert.ToInt64(
            await command.ExecuteScalarAsync(TestContext.CancellationToken),
            CultureInfo.InvariantCulture));
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task TransitionRunToTerminalAsync_WithConcurrentWriters_CommitsExactlyOneTerminalState()
    {
        const int iterationCount = 10;
        var root = Path.Combine(
            Path.GetTempPath(),
            "madorin-runtime-terminal-race-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var connectionString = $"Data Source={Path.Combine(root, "state.db")};Pooling=False";
        try
        {
            await using var setupConnection = new SqliteConnection(connectionString);
            await setupConnection.OpenAsync(TestContext.CancellationToken);
            await SqliteSchema.EnsureCreatedAsync(
                setupConnection,
                TestContext.CancellationToken);

            for (var iteration = 0; iteration < iterationCount; iteration++)
            {
                var (setupRepository, runId) = await CreateRunAsync(
                    setupConnection,
                    TestContext.CancellationToken);
                await setupRepository.TransitionRunStatusAsync(
                    runId,
                    RunStatus.Accepted,
                    RunStatus.Preparing,
                    TestContext.CancellationToken);
                await setupRepository.TransitionRunStatusAsync(
                    runId,
                    RunStatus.Preparing,
                    RunStatus.Running,
                    TestContext.CancellationToken);
                await setupRepository.TransitionRunStatusAsync(
                    runId,
                    RunStatus.Running,
                    RunStatus.Persisting,
                    TestContext.CancellationToken);

                TerminalAttempt[] attempts =
                [
                    new(RunStatus.Completed, $"completed-{iteration}-1"),
                    new(RunStatus.Failed, $"timeout-{iteration}-1"),
                    new(RunStatus.Cancelled, $"cancelled-{iteration}-1"),
                    new(RunStatus.Completed, $"completed-{iteration}-2"),
                    new(RunStatus.Failed, $"disconnect-{iteration}-2"),
                    new(RunStatus.Cancelled, $"cancelled-{iteration}-2")
                ];
                var readyCount = 0;
                var ready = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var start = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                async Task<TerminalAttempt?> CompeteAsync(TerminalAttempt attempt)
                {
                    await using var connection = new SqliteConnection(connectionString);
                    await connection.OpenAsync(TestContext.CancellationToken);
                    await using (var busyTimeout = connection.CreateCommand())
                    {
                        busyTimeout.CommandText = "PRAGMA busy_timeout = 5000;";
                        await busyTimeout.ExecuteNonQueryAsync(TestContext.CancellationToken);
                    }

                    if (Interlocked.Increment(ref readyCount) == attempts.Length)
                    {
                        ready.TrySetResult();
                    }

                    await start.Task.WaitAsync(TestContext.CancellationToken);
                    var repository = new SqliteSessionRepository(connection);
                    try
                    {
                        await repository.TransitionRunToTerminalAsync(
                            runId,
                            RunStatus.Persisting,
                            attempt.Status,
                            attempt.Text,
                            TestContext.CancellationToken);
                        return attempt;
                    }
                    catch (InvalidOperationException)
                    {
                        return null;
                    }
                }

                var competitors = attempts.Select(CompeteAsync).ToArray();
                await ready.Task.WaitAsync(TestContext.CancellationToken);
                start.TrySetResult();
                var winners = (await Task.WhenAll(competitors))
                    .OfType<TerminalAttempt>()
                    .ToArray();

                Assert.HasCount(1, winners);
                var snapshot = await setupRepository.GetRunSnapshotAsync(
                    runId,
                    TestContext.CancellationToken);
                Assert.IsNotNull(snapshot);
                Assert.AreEqual(winners[0].Status, snapshot.Status);
                Assert.AreEqual(winners[0].Text, snapshot.TerminalText);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task EnsureCreatedAsync_FromVersionFive_AddsTerminalTextWithoutLosingRuns()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        await DowngradeSchemaAsync(connection, 5, TestContext.CancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO sessions(session_id, mode, status, created_at, updated_at)
                VALUES('session-v5', 'Expert', 'Active', '2026-07-22T00:00:00Z', '2026-07-22T00:00:00Z');
                INSERT INTO runs(run_id, session_id, status, run_sequence, created_at, updated_at)
                VALUES('run-v5', 'session-v5', 'Completed', 1, '2026-07-22T00:00:00Z', '2026-07-22T00:00:00Z');
                """;
            await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }

        await SqliteSchema.EnsureCreatedAsync(connection, TestContext.CancellationToken);

        var repository = new SqliteSessionRepository(connection);
        var snapshot = await repository.GetRunSnapshotAsync(
            "run-v5",
            TestContext.CancellationToken);
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(RunStatus.Completed, snapshot.Status);
        Assert.IsNull(snapshot.TerminalText);
        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "SELECT MAX(version) FROM schema_versions;";
        Assert.AreEqual(
            SqliteSchema.CurrentVersion,
            Convert.ToInt32(
                await versionCommand.ExecuteScalarAsync(TestContext.CancellationToken),
                CultureInfo.InvariantCulture));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    public async Task EnsureCreatedAsync_FromEveryHistoricalVersion_PreservesExistingRun(
        int historicalVersion)
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        await DowngradeSchemaAsync(
            connection,
            historicalVersion,
            TestContext.CancellationToken);
        var sessionId = $"session-v{historicalVersion}";
        var runId = $"run-v{historicalVersion}";
        await InsertHistoricalRunAsync(
            connection,
            sessionId,
            runId,
            TestContext.CancellationToken);

        await SqliteSchema.EnsureCreatedAsync(connection, TestContext.CancellationToken);

        var repository = new SqliteSessionRepository(connection);
        var snapshot = await repository.GetRunSnapshotAsync(
            runId,
            TestContext.CancellationToken);
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(sessionId, snapshot.SessionId);
        Assert.AreEqual(RunStatus.Running, snapshot.Status);
        Assert.IsNull(snapshot.TerminalText);
        Assert.AreEqual(
            SqliteSchema.CurrentVersion,
            await GetSchemaVersionAsync(connection, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task EnsureCreatedAsync_WhenMigrationFails_RollsBackVersionAndPreservesData()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        await DowngradeSchemaAsync(connection, 4, TestContext.CancellationToken);
        await InsertHistoricalRunAsync(
            connection,
            "session-failed-migration",
            "run-failed-migration",
            TestContext.CancellationToken);
        await using (var conflict = connection.CreateCommand())
        {
            conflict.CommandText =
                "ALTER TABLE session_idempotency ADD COLUMN request_hash TEXT;";
            await conflict.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }

        await Assert.ThrowsExactlyAsync<SqliteException>(
            async () => await SqliteSchema.EnsureCreatedAsync(
                connection,
                TestContext.CancellationToken));

        Assert.AreEqual(
            4,
            await GetSchemaVersionAsync(connection, TestContext.CancellationToken));
        await using var preservedData = connection.CreateCommand();
        preservedData.CommandText = """
            SELECT COUNT(*)
            FROM runs
            WHERE run_id = 'run-failed-migration'
              AND session_id = 'session-failed-migration'
              AND status = 'Running';
            """;
        Assert.AreEqual(
            1L,
            Convert.ToInt64(
                await preservedData.ExecuteScalarAsync(TestContext.CancellationToken),
                CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public async Task MarkInterruptedAsync_WithRunningRun_MarksRunInterrupted()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var (repository, runId) = await CreateRunAsync(connection, TestContext.CancellationToken);
        await repository.TransitionRunStatusAsync(
            runId,
            RunStatus.Accepted,
            RunStatus.Preparing,
            TestContext.CancellationToken);
        await repository.TransitionRunStatusAsync(
            runId,
            RunStatus.Preparing,
            RunStatus.Running,
            TestContext.CancellationToken);

        await repository.MarkInterruptedAsync(TestContext.CancellationToken);

        Assert.AreEqual(
            RunStatus.Interrupted,
            await repository.GetRunStatusAsync(runId, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task MarkInterruptedAsync_WithWaitingForApprovalRun_PreservesWaitingState()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var (repository, runId) = await CreateRunAsync(connection, TestContext.CancellationToken);
        await repository.TransitionRunStatusAsync(
            runId,
            RunStatus.Accepted,
            RunStatus.Preparing,
            TestContext.CancellationToken);
        await repository.TransitionRunStatusAsync(
            runId,
            RunStatus.Preparing,
            RunStatus.Running,
            TestContext.CancellationToken);
        await repository.TransitionRunStatusAsync(
            runId,
            RunStatus.Running,
            RunStatus.WaitingForApproval,
            TestContext.CancellationToken);

        await repository.MarkInterruptedAsync(TestContext.CancellationToken);

        Assert.AreEqual(
            RunStatus.WaitingForApproval,
            await repository.GetRunStatusAsync(runId, TestContext.CancellationToken));
    }


    [TestMethod]
    public async Task ListSessionsAsync_100kSessions_UsesKeysetIndexWithinBaseline()
    {
        const int sessionCount = 100_000;
        var root = Path.Combine(
            Path.GetTempPath(),
            "madorin-runtime-pagination",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "state.db");
        SqliteConnection? connection = null;
        try
        {
            connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
            await connection.OpenAsync(TestContext.CancellationToken);
            await SqliteSchema.EnsureCreatedAsync(connection, TestContext.CancellationToken);

            using (var transaction = connection.BeginTransaction())
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO sessions(session_id, mode, status, created_at, updated_at)
                    VALUES($sessionId, $mode, $status, $createdAt, $updatedAt);
                    """;
                var sessionId = insert.Parameters.Add("$sessionId", SqliteType.Text);
                var mode = insert.Parameters.Add("$mode", SqliteType.Text);
                var status = insert.Parameters.Add("$status", SqliteType.Text);
                var createdAt = insert.Parameters.Add("$createdAt", SqliteType.Text);
                var updatedAt = insert.Parameters.Add("$updatedAt", SqliteType.Text);
                mode.Value = RuntimeMode.Expert.ToString();
                status.Value = SessionStatus.Active.ToString();
                for (var index = 0; index < sessionCount; index++)
                {
                    var timestamp = DateTimeOffset.UnixEpoch.AddSeconds(index)
                        .ToString("O", CultureInfo.InvariantCulture);
                    sessionId.Value = $"session-{index:D6}";
                    createdAt.Value = timestamp;
                    updatedAt.Value = timestamp;
                    await insert.ExecuteNonQueryAsync(TestContext.CancellationToken);
                }

                transaction.Commit();
            }

            await using (var plan = connection.CreateCommand())
            {
                plan.CommandText = """
                    EXPLAIN QUERY PLAN
                    SELECT session_id, mode, status, updated_at
                    FROM sessions
                    WHERE updated_at < $updatedAt
                       OR (updated_at = $updatedAt AND session_id < $sessionId)
                    ORDER BY updated_at DESC, session_id DESC
                    LIMIT $pageSize;
                    """;
                plan.Parameters.AddWithValue(
                    "$updatedAt",
                    DateTimeOffset.UnixEpoch.AddSeconds(sessionCount / 2)
                        .ToString("O", CultureInfo.InvariantCulture));
                plan.Parameters.AddWithValue("$sessionId", "session-050000");
                plan.Parameters.AddWithValue("$pageSize", 100);
                await using var planReader = await plan.ExecuteReaderAsync(
                    TestContext.CancellationToken);
                var details = new List<string>();
                while (await planReader.ReadAsync(TestContext.CancellationToken))
                {
                    details.Add(planReader.GetString(3));
                }

                StringAssert.Contains(string.Join("\n", details), "idx_sessions_updated");
            }

            var repository = new SqliteSessionRepository(connection);
            var cursor = (string?)null;
            var total = 0;
            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                var page = await repository.ListSessionsAsync(
                    cursor,
                    pageSize: 500,
                    TestContext.CancellationToken);
                if (page.Count == 0)
                {
                    break;
                }

                total += page.Count;
                var last = page[^1];
                cursor = last.UpdatedAt.ToUniversalTime().ToString(
                    "O",
                    CultureInfo.InvariantCulture) + "|" + last.SessionId;
            }

            stopwatch.Stop();
            Assert.AreEqual(sessionCount, total);
            Assert.IsTrue(
                stopwatch.Elapsed < TimeSpan.FromSeconds(30),
                $"100k Session keyset pagination took {stopwatch.Elapsed}.");
            TestContext.WriteLine(
                $"session-pagination count={sessionCount} pages={sessionCount / 500} elapsed={stopwatch.Elapsed.TotalMilliseconds:F0}ms");
        }
        finally
        {
            if (connection is not null)
            {
                connection.Close();
                await connection.DisposeAsync();
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task TrySaveSessionSelectionAsync_WithVersionConflict_PreservesCurrentSelection()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var repository = new SqliteSessionRepository(connection);
        var sessionId = await repository.CreateSessionAsync(
            RuntimeMode.Expert,
            "selection-session",
            KeyRetention,
            TestContext.CancellationToken);
        var versionOneAgents = new[]
        {
            new AgentSnapshot("agent-1", "v1", "hash-v1", "provider-a", "model-a")
        };

        Assert.IsTrue(await repository.TrySaveSessionSelectionAsync(
            sessionId,
            expectedSelectionVersion: null,
            selectionVersion: 1,
            "{\"selectionVersion\":1}",
            versionOneAgents,
            TestContext.CancellationToken));
        Assert.IsFalse(await repository.TrySaveSessionSelectionAsync(
            sessionId,
            expectedSelectionVersion: 0,
            selectionVersion: 2,
            "{\"selectionVersion\":2}",
            versionOneAgents,
            TestContext.CancellationToken));
        Assert.IsTrue(await repository.TrySaveSessionSelectionAsync(
            sessionId,
            expectedSelectionVersion: 1,
            selectionVersion: 2,
            "{\"selectionVersion\":2}",
            [new AgentSnapshot("agent-1", "v2", "hash-v2", "provider-b", "model-b")],
            TestContext.CancellationToken));
        Assert.IsTrue(await repository.TrySaveSessionSelectionAsync(
            sessionId,
            expectedSelectionVersion: 1,
            selectionVersion: 2,
            "{\"selectionVersion\":2}",
            [new AgentSnapshot("agent-1", "v2", "hash-v2", "provider-b", "model-b")],
            TestContext.CancellationToken));

        var snapshot = await repository.GetSessionSnapshotAsync(
            sessionId,
            TestContext.CancellationToken);
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(2, snapshot.SelectionVersion);
        Assert.AreEqual("{\"selectionVersion\":2}", snapshot.SelectionJson);
        Assert.HasCount(1, snapshot.AgentSnapshots);
        Assert.AreEqual("hash-v2", snapshot.AgentSnapshots[0].PromptHash);
    }

    [TestMethod]
    public async Task SaveInvocationSnapshotAsync_WithSameSnapshot_IsIdempotent()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var repository = new SqliteSessionRepository(connection);
        var sessionId = await repository.CreateSessionAsync(
            RuntimeMode.Expert,
            "snapshot-idempotent-session",
            KeyRetention,
            TestContext.CancellationToken);
        var runId = await repository.CreateRunAsync(
            sessionId,
            "snapshot-idempotent-run",
            KeyRetention,
            TestContext.CancellationToken);
        var snapshot = new InvocationSnapshot(
            "invocation-1",
            "agent-1",
            "provider-1",
            "model-1",
            "prompt-hash-1",
            "global-memory-hash-1",
            "project-memory-hash-1",
            "tools-v1",
            "projection-v1",
            new DateTimeOffset(2026, 7, 23, 1, 2, 3, TimeSpan.Zero));
        const string snapshotJson = """{"invocationId":"invocation-1","version":1}""";

        await repository.SaveInvocationSnapshotAsync(
            runId,
            sessionId,
            snapshot,
            snapshotJson,
            TestContext.CancellationToken);
        await repository.SaveInvocationSnapshotAsync(
            runId,
            sessionId,
            snapshot,
            snapshotJson,
            TestContext.CancellationToken);

        Assert.AreEqual(
            snapshotJson,
            await repository.GetInvocationSnapshotJsonAsync(
                snapshot.InvocationId,
                TestContext.CancellationToken));
        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText =
            "SELECT COUNT(*) FROM invocation_snapshots WHERE invocation_id = $invocationId;";
        countCommand.Parameters.AddWithValue("$invocationId", snapshot.InvocationId);
        Assert.AreEqual(
            1L,
            Convert.ToInt64(
                await countCommand.ExecuteScalarAsync(TestContext.CancellationToken),
                CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public async Task SaveInvocationSnapshotAsync_WithChangedImmutableField_RejectsAndPreservesOriginal()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var repository = new SqliteSessionRepository(connection);
        var sessionId = await repository.CreateSessionAsync(
            RuntimeMode.Expert,
            "snapshot-immutable-session",
            KeyRetention,
            TestContext.CancellationToken);
        var runId = await repository.CreateRunAsync(
            sessionId,
            "snapshot-immutable-run",
            KeyRetention,
            TestContext.CancellationToken);
        var otherRunId = await repository.CreateRunAsync(
            sessionId,
            "snapshot-other-run",
            KeyRetention,
            TestContext.CancellationToken);
        var otherSessionId = await repository.CreateSessionAsync(
            RuntimeMode.Expert,
            "snapshot-other-session",
            KeyRetention,
            TestContext.CancellationToken);
        var snapshot = new InvocationSnapshot(
            "invocation-immutable",
            "agent-1",
            "provider-1",
            "model-1",
            "prompt-hash-1",
            null,
            null,
            "tools-v1",
            "projection-v1",
            new DateTimeOffset(2026, 7, 23, 4, 5, 6, TimeSpan.Zero));
        const string snapshotJson = """{"invocationId":"invocation-immutable","version":1}""";
        const string changedSnapshotJson = """{"invocationId":"invocation-immutable","version":2}""";
        await repository.SaveInvocationSnapshotAsync(
            runId,
            sessionId,
            snapshot,
            snapshotJson,
            TestContext.CancellationToken);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            repository.SaveInvocationSnapshotAsync(
                runId,
                sessionId,
                snapshot,
                changedSnapshotJson,
                TestContext.CancellationToken));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            repository.SaveInvocationSnapshotAsync(
                otherRunId,
                sessionId,
                snapshot,
                snapshotJson,
                TestContext.CancellationToken));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            repository.SaveInvocationSnapshotAsync(
                runId,
                otherSessionId,
                snapshot,
                snapshotJson,
                TestContext.CancellationToken));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            repository.SaveInvocationSnapshotAsync(
                runId,
                sessionId,
                snapshot with { StartedAt = snapshot.StartedAt.AddSeconds(1) },
                snapshotJson,
                TestContext.CancellationToken));

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT run_id, session_id, snapshot_json, started_at
            FROM invocation_snapshots
            WHERE invocation_id = $invocationId;
            """;
        command.Parameters.AddWithValue("$invocationId", snapshot.InvocationId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        Assert.IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        Assert.AreEqual(runId, reader.GetString(0));
        Assert.AreEqual(sessionId, reader.GetString(1));
        Assert.AreEqual(snapshotJson, reader.GetString(2));
        Assert.AreEqual(
            snapshot.StartedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            reader.GetString(3));
        Assert.IsFalse(await reader.ReadAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task ListMessageIndexAsync_WithCursor_ReturnsOrderedMetadataPage()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var repository = new SqliteSessionRepository(connection);
        var sessionId = await repository.CreateSessionAsync(
            RuntimeMode.Expert,
            "message-session",
            KeyRetention,
            TestContext.CancellationToken);
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO message_index(
                    message_id,
                    session_id,
                    sequence,
                    invocation_id,
                    agent_id,
                    role,
                    file_offset,
                    record_length,
                    created_at)
                VALUES
                    ('message-1', $sessionId, 1, 'invocation-1', 'agent-1', 'user', 100, 20, '2026-07-22T00:00:00.0000000+00:00'),
                    ('message-2', $sessionId, 2, 'invocation-1', 'agent-1', 'assistant', 120, 30, '2026-07-22T00:00:01.0000000+00:00'),
                    ('message-3', $sessionId, 3, 'invocation-2', 'agent-1', 'user', 150, 25, '2026-07-22T00:00:02.0000000+00:00');
                """;
            insert.Parameters.AddWithValue("$sessionId", sessionId);
            await insert.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }

        var firstPage = await repository.ListMessageIndexAsync(
            sessionId,
            cursor: null,
            pageSize: 2,
            TestContext.CancellationToken);
        var secondPage = await repository.ListMessageIndexAsync(
            sessionId,
            cursor: firstPage[^1].Sequence,
            pageSize: 2,
            TestContext.CancellationToken);

        CollectionAssert.AreEqual(
            new long[] { 1, 2 },
            firstPage.Select(static entry => entry.Sequence).ToArray());
        CollectionAssert.AreEqual(
            new long[] { 3 },
            secondPage.Select(static entry => entry.Sequence).ToArray());
        Assert.AreEqual("assistant", firstPage[1].Role);
    }

    private static async Task<(SqliteSessionRepository Repository, string RunId)> CreateRunAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        var repository = new SqliteSessionRepository(connection);
        var sessionId = await repository.CreateSessionAsync(
            RuntimeMode.Expert,
            Guid.NewGuid().ToString("N"),
            KeyRetention,
            ct);
        var runId = await repository.CreateRunAsync(
            sessionId,
            Guid.NewGuid().ToString("N"),
            KeyRetention,
            ct);
        return (repository, runId);
    }

    private static async Task<SqliteConnection> CreateDatabaseAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        await SqliteSchema.EnsureCreatedAsync(connection, ct);
        return connection;
    }

    private static async Task DowngradeSchemaAsync(
        SqliteConnection connection,
        int targetVersion,
        CancellationToken ct)
    {
        // Note: Some columns may already be removed by later migrations (e.g., depth removed in v20).
        // We must check if columns exist before dropping them to avoid "no such column" errors.

        if (targetVersion < 13)
        {
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    DROP INDEX IF EXISTS idx_work_plan_revisions_session;
                    DROP TABLE IF EXISTS work_plan_revisions;
                    """;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await DropColumnIfExistsAsync(connection, "work_steps", "step_message_id", ct);
            await DropColumnIfExistsAsync(connection, "work_steps", "reuses_step_id", ct);
            await DropColumnIfExistsAsync(connection, "work_steps", "replaces_step_id", ct);
            await DropColumnIfExistsAsync(connection, "work_steps", "checkpoint_json", ct);
        }

        if (targetVersion < 12)
        {
            await DropColumnIfExistsAsync(connection, "tool_audit", "session_id", ct);
            await DropColumnIfExistsAsync(connection, "tool_audit", "invocation_id", ct);
            await DropColumnIfExistsAsync(connection, "tool_audit", "parent_invocation_id", ct);
            await DropColumnIfExistsAsync(connection, "tool_audit", "work_step_id", ct);
            await DropColumnIfExistsAsync(connection, "tool_audit", "plan_version", ct);
            await DropColumnIfExistsAsync(connection, "tool_audit", "root_grant_id", ct);
        }

        if (targetVersion < 11)
        {
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    DROP INDEX IF EXISTS idx_work_sessions_status;
                    DROP INDEX IF EXISTS idx_work_steps_session;
                    DROP INDEX IF EXISTS idx_work_steps_run;
                    DROP INDEX IF EXISTS idx_work_steps_status;
                    DROP INDEX IF EXISTS idx_work_steps_agent;
                    DROP INDEX IF EXISTS idx_work_step_dependencies_step;
                    DROP INDEX IF EXISTS idx_work_step_dependencies_depends;
                    DROP INDEX IF EXISTS idx_work_step_attempts_step;
                    DROP INDEX IF EXISTS idx_work_background_jobs_session;
                    DROP INDEX IF EXISTS idx_work_background_jobs_step;
                    DROP INDEX IF EXISTS idx_tool_intents_work_step;
                    DROP TABLE IF EXISTS work_sessions;
                    DROP TABLE IF EXISTS work_step_dependencies;
                    DROP TABLE IF EXISTS work_step_attempts;
                    DROP TABLE IF EXISTS work_background_jobs;
                    """;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await DropColumnIfExistsAsync(connection, "work_steps", "parent_step_id", ct);
            await DropColumnIfExistsAsync(connection, "work_steps", "invocation_id", ct);
            await DropColumnIfExistsAsync(connection, "work_steps", "depth", ct);  // May already be removed in v20
            await DropColumnIfExistsAsync(connection, "work_steps", "step_index", ct);
            await DropColumnIfExistsAsync(connection, "work_steps", "attempt_count", ct);
            await DropColumnIfExistsAsync(connection, "work_steps", "error_message", ct);
            await DropColumnIfExistsAsync(connection, "work_steps", "started_at", ct);
            await DropColumnIfExistsAsync(connection, "work_steps", "title", ct);
            await DropColumnIfExistsAsync(connection, "work_steps", "goal", ct);
            await DropColumnIfExistsAsync(connection, "work_steps", "is_background", ct);
            await DropColumnIfExistsAsync(connection, "work_steps", "depends_json", ct);
            await DropColumnIfExistsAsync(connection, "tool_intents", "work_step_id", ct);
            await DropColumnIfExistsAsync(connection, "tool_intents", "plan_version", ct);
        }

        if (targetVersion < 8)
        {
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    DROP TABLE IF EXISTS tool_audit;
                    DROP TABLE IF EXISTS tool_approvals;
                    DROP TABLE IF EXISTS tool_grants;
                    DROP INDEX IF EXISTS idx_tool_intents_status;
                    DROP INDEX IF EXISTS idx_tool_intents_run;
                    """;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await DropColumnIfExistsAsync(connection, "tool_intents", "agent_id", ct);
            await DropColumnIfExistsAsync(connection, "tool_intents", "parent_agent_id", ct);
            await DropColumnIfExistsAsync(connection, "tool_intents", "catalog_version", ct);
            await DropColumnIfExistsAsync(connection, "tool_intents", "sent_at", ct);
            await DropColumnIfExistsAsync(connection, "tool_intents", "grant_id", ct);
            await DropColumnIfExistsAsync(connection, "tool_intents", "approval_request_id", ct);
            await DropColumnIfExistsAsync(connection, "tool_intents", "result_hash", ct);
            await DropColumnIfExistsAsync(connection, "tool_intents", "result_blob_id", ct);
            await DropColumnIfExistsAsync(connection, "tool_intents", "result_blob_length", ct);
            await DropColumnIfExistsAsync(connection, "tool_intents", "result_blob_sha256", ct);
            await DropColumnIfExistsAsync(connection, "tool_intents", "result_blob_content_type", ct);
            await DropColumnIfExistsAsync(connection, "tool_intents", "result_blob_access_scope", ct);
            await DropColumnIfExistsAsync(connection, "tool_intents", "result_blob_expires_at", ct);
            await DropColumnIfExistsAsync(connection, "tool_intents", "error_code", ct);
            await DropColumnIfExistsAsync(connection, "tool_intents", "result_visible", ct);
        }

        if (targetVersion < 7)
        {
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    DROP TABLE IF EXISTS agent_snapshots;
                    DROP TABLE IF EXISTS session_selections;
                    """;
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        if (targetVersion < 6)
        {
            await DropColumnIfExistsAsync(connection, "runs", "terminal_text", ct);
        }

        if (targetVersion < 5)
        {
            await DropColumnIfExistsAsync(connection, "session_idempotency", "request_hash", ct);
            await DropColumnIfExistsAsync(connection, "run_idempotency", "request_hash", ct);
        }

        if (targetVersion < 4)
        {
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "DROP TABLE IF EXISTS work_steps;";
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        if (targetVersion < 3)
        {
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "DROP TABLE IF EXISTS message_index;";
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        if (targetVersion < 2)
        {
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "DROP TABLE IF EXISTS tool_intents;";
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        await using (var versionCommand = connection.CreateCommand())
        {
            versionCommand.CommandText = "DELETE FROM schema_versions WHERE version > $targetVersion;";
            versionCommand.Parameters.AddWithValue("$targetVersion", targetVersion);
            await versionCommand.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task DropColumnIfExistsAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        CancellationToken ct)
    {
        // Check if column exists using pragma_table_info
        await using var checkCommand = connection.CreateCommand();
        checkCommand.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{tableName}') WHERE name = $columnName;";
        checkCommand.Parameters.AddWithValue("$columnName", columnName);
        var result = await checkCommand.ExecuteScalarAsync(ct);
        var columnExists = Convert.ToInt32(result, CultureInfo.InvariantCulture) > 0;

        if (columnExists)
        {
            await using var dropCommand = connection.CreateCommand();
            dropCommand.CommandText = $"ALTER TABLE {tableName} DROP COLUMN {columnName};";
            await dropCommand.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task InsertHistoricalRunAsync(
        SqliteConnection connection,
        string sessionId,
        string runId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions(session_id, mode, status, created_at, updated_at)
            VALUES($sessionId, 'Expert', 'Active', '2026-07-22T00:00:00Z', '2026-07-22T00:00:00Z');
            INSERT INTO runs(run_id, session_id, status, run_sequence, created_at, updated_at)
            VALUES($runId, $sessionId, 'Running', 1, '2026-07-22T00:00:00Z', '2026-07-22T00:00:00Z');
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$runId", runId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> GetSchemaVersionAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(version) FROM schema_versions;";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(ct),
            CultureInfo.InvariantCulture);
    }

    private static async Task<string> ReadStepInputHashAsync(
        SqliteConnection connection,
        string stepId,
        string planVersion,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT step_input_hash
            FROM work_steps
            WHERE step_id = $stepId AND plan_version = $planVersion;
            """;
        command.Parameters.AddWithValue("$stepId", stepId);
        command.Parameters.AddWithValue("$planVersion", planVersion);
        return (string?)await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidDataException($"Work step '{stepId}' did not have an input hash.");
    }

    private static async Task InsertSentToolIntentAsync(
        SqliteConnection connection,
        string sessionId,
        string runId,
        string stepId,
        string planVersion,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tool_intents(
                call_id, invocation_id, run_id, session_id, tool_id,
                arguments_hash, status, created_at, agent_id, parent_agent_id,
                catalog_version, sent_at, work_step_id, plan_version)
            VALUES(
                'call-step-a', 'invocation-a', $runId, $sessionId, 'builtin.test',
                'arguments-hash', 'sent', '2026-07-23T00:00:00.000Z',
                'worker', 'manager', 'tools-v1', '2026-07-23T00:00:01.000Z',
                $stepId, $planVersion);
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$stepId", stepId);
        command.Parameters.AddWithValue("$planVersion", planVersion);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task BackdateBackgroundJobAsync(
        SqliteConnection connection,
        string jobId,
        string timestamp,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE work_background_jobs
            SET created_at = $timestamp,
                started_at = $timestamp
            WHERE job_id = $jobId;
            """;
        command.Parameters.AddWithValue("$jobId", jobId);
        command.Parameters.AddWithValue("$timestamp", timestamp);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string[]> ListTableNamesAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_master
            WHERE type = 'table'
            ORDER BY name;
            """;
        return await ReadStringArrayAsync(command, ct);
    }

    private static async Task<string[]> ListColumnNamesAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            names.Add(reader.GetString(1));
        }

        return [.. names];
    }

    private static async Task<string[]> ListIndexNamesAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_master
            WHERE type = 'index'
            ORDER BY name;
            """;
        return await ReadStringArrayAsync(command, ct);
    }

    private static async Task<string[]> ReadStringArrayAsync(
        SqliteCommand command,
        CancellationToken ct)
    {
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            values.Add(reader.GetString(0));
        }

        return [.. values];
    }

    private sealed record TerminalAttempt(RunStatus Status, string Text);
}
