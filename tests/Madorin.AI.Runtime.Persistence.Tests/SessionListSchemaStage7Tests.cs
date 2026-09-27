using System.Globalization;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Persistence.Tests;

[TestClass]
public sealed class SessionListSchemaStage7Tests
{
    private readonly TestContext _testContext;

    public SessionListSchemaStage7Tests(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestMethod]
    public async Task EnsureCreatedAsync_MigratesFromSchema13ToCurrent_PreservesData()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var ct = _testContext.CancellationToken;
        await connection.OpenAsync(ct);

        await SqliteSchema.EnsureCreatedAsync(connection, ct);

        var sessionId = "schema-test-session-001";
        var runId = "schema-test-run-001";
        var createdAt = "2025-01-15T10:30:00+00:00";
        var updatedAt = "2025-06-20T14:45:00+00:00";

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO sessions (session_id, mode, status, created_at, updated_at, title)
                VALUES ($sid, $mode, $status, $created, $updated, $title);
                """;
            cmd.Parameters.AddWithValue("$sid", sessionId);
            cmd.Parameters.AddWithValue("$mode", "Expert");
            cmd.Parameters.AddWithValue("$status", "Active");
            cmd.Parameters.AddWithValue("$created", createdAt);
            cmd.Parameters.AddWithValue("$updated", updatedAt);
            cmd.Parameters.AddWithValue("$title", "Original Title");
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO runs (run_id, session_id, status, run_sequence, created_at, updated_at)
                VALUES ($rid, $sid, $status, $seq, $created, $updated);
                """;
            cmd.Parameters.AddWithValue("$rid", runId);
            cmd.Parameters.AddWithValue("$sid", sessionId);
            cmd.Parameters.AddWithValue("$status", "Completed");
            cmd.Parameters.AddWithValue("$seq", 1);
            cmd.Parameters.AddWithValue("$created", createdAt);
            cmd.Parameters.AddWithValue("$updated", updatedAt);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM schema_versions WHERE version > 13;";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "ALTER TABLE sessions DROP COLUMN title;";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_versions;";
            var version = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            Assert.AreEqual(13, version, "Database should be at schema version 13 before migration.");
        }

        await SqliteSchema.EnsureCreatedAsync(connection, ct);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_versions;";
            var version = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            Assert.AreEqual(
                SqliteSchema.CurrentVersion,
                version,
                "Database should be migrated to the current schema version.");
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(sessions);";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var titleColumnFound = false;
            while (await reader.ReadAsync(ct))
            {
                if (reader.GetString(1) == "title")
                {
                    titleColumnFound = true;
                    Assert.AreEqual("TEXT", reader.GetString(2), "title column type should be TEXT.");
                    Assert.AreEqual(0, reader.GetInt32(3), "title column should be nullable (notnull=0).");
                    break;
                }
            }
            Assert.IsTrue(titleColumnFound, "sessions.title column must exist after migration to v14.");
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT session_id, mode, status, created_at, updated_at
                FROM sessions WHERE session_id = $sid;
                """;
            cmd.Parameters.AddWithValue("$sid", sessionId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            Assert.IsTrue(await reader.ReadAsync(ct), "Session must survive migration.");
            Assert.AreEqual(sessionId, reader.GetString(0));
            Assert.AreEqual("Expert", reader.GetString(1));
            Assert.AreEqual("Active", reader.GetString(2));
            Assert.AreEqual(createdAt, reader.GetString(3));
            Assert.AreEqual(updatedAt, reader.GetString(4));
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT run_id, session_id, status, run_sequence
                FROM runs WHERE run_id = $rid;
                """;
            cmd.Parameters.AddWithValue("$rid", runId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            Assert.IsTrue(await reader.ReadAsync(ct), "Run must survive migration.");
            Assert.AreEqual(runId, reader.GetString(0));
            Assert.AreEqual(sessionId, reader.GetString(1));
            Assert.AreEqual("Completed", reader.GetString(2));
            Assert.AreEqual(1, reader.GetInt32(3));
        }
    }

    [TestMethod]
    public async Task EnsureCreatedAsync_RepairsWorkPendingInputsMissingAfterVersion19()
    {
        var ct = _testContext.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        await SqliteSchema.EnsureCreatedAsync(connection, ct);

        await using (var drop = connection.CreateCommand())
        {
            drop.CommandText = "DROP TABLE work_pending_inputs;";
            await drop.ExecuteNonQueryAsync(ct);
        }

        await SqliteSchema.EnsureCreatedAsync(connection, ct);

        await using var table = connection.CreateCommand();
        table.CommandText = """
            SELECT 1 FROM sqlite_master
            WHERE type = 'table' AND name = 'work_pending_inputs';
            """;
        Assert.IsNotNull(await table.ExecuteScalarAsync(ct));

        await using var index = connection.CreateCommand();
        index.CommandText = """
            SELECT 1 FROM sqlite_master
            WHERE type = 'index' AND name = 'idx_work_pending_inputs_session_state';
            """;
        Assert.IsNotNull(await index.ExecuteScalarAsync(ct));
    }
}
