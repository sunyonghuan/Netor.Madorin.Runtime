using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Modes.Tests;

[TestClass]
public sealed class MeetingPendingUserInputTests
{
    private readonly TestContext _testContext;

    public MeetingPendingUserInputTests(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestMethod]
    public async Task PendingInputQueue_EnqueuePeekConsume_UsesFifoOrder()
    {
        var ct = _testContext.CancellationToken;
        await using var connection = await OpenMeetingDbAsync(ct);
        var meetingRepo = new SqliteMeetingRepository(connection);

        await meetingRepo.EnqueuePendingUserInputAsync("meeting-1", "first", ct: ct);
        await meetingRepo.EnqueuePendingUserInputAsync("meeting-1", "second", ct: ct);

        var peeked = await meetingRepo.PeekPendingUserInputsAsync("meeting-1", ct: ct);
        Assert.HasCount(2, peeked);
        Assert.AreEqual("first", peeked[0].ContentText);
        Assert.AreEqual("second", peeked[1].ContentText);
        Assert.AreEqual(MeetingPendingUserInputState.Pending, peeked[0].State);

        var stillPending = await meetingRepo.PeekPendingUserInputsAsync("meeting-1", ct: ct);
        Assert.HasCount(2, stillPending);

        var consumed = await meetingRepo.ConsumePendingUserInputsAsync(
            "meeting-1",
            "run-consume",
            limit: 1,
            ct: ct);
        Assert.HasCount(1, consumed);
        Assert.AreEqual("first", consumed[0].ContentText);
        Assert.AreEqual(MeetingPendingUserInputState.Consumed, consumed[0].State);
        Assert.AreEqual("run-consume", consumed[0].ConsumedByRunId);

        var remaining = await meetingRepo.PeekPendingUserInputsAsync("meeting-1", ct: ct);
        Assert.HasCount(1, remaining);
        Assert.AreEqual("second", remaining[0].ContentText);
        Assert.AreEqual(1, await meetingRepo.CountPendingUserInputsAsync("meeting-1", ct));
    }

    private static async Task<SqliteConnection> OpenMeetingDbAsync(CancellationToken ct)
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"meeting-pending-db-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataDirectory, "state.db"),
            Pooling = false
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        await SqliteSchema.EnsureCreatedAsync(connection, ct);
        return connection;
    }
}
