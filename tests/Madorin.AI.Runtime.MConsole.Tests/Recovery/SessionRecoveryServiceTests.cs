using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.MConsole.Recovery;
using Madorin.AI.Runtime.MConsole.Tests.TestSupport;

namespace Madorin.AI.Runtime.MConsole.Tests.Recovery;

[TestClass]
public sealed class SessionRecoveryServiceTests
{
    public TestContext TestContext { get; set; }

    [TestMethod]
    public async Task ResumeAndRehydrateAsync_ValidatesReadyStateAndQueriesLatestRun()
    {
        var client = new FakeRuntimeClient
        {
            ResumeResult = new SessionResumeResult(
                "session",
                RuntimeMode.Expert,
                null,
                null,
                [],
                new RunQueryResult("run", "session", RunStatus.Completed, "done"),
                12,
                null,
                IsFullyRecoverable: true,
                RecoveryDiagnostics: []),
            RehydrateResult = new SessionRehydrateResult("session", "Ready", []),
            QueryResult = new RunQueryResult("run", "session", RunStatus.Completed, "done"),
        };
        var service = new SessionRecoveryService(client);

        var result = await service.ResumeAndRehydrateAsync(
            "session",
            [],
            TestContext.CancellationToken);

        Assert.AreEqual("Ready", result.Rehydrate.Status);
        Assert.AreEqual(RunStatus.Completed, result.LatestRun!.Status);
        Assert.AreEqual(12, result.Resume.LastGsn);
    }

    [TestMethod]
    public async Task ResumeAndRehydrateAsync_UnrecoverableSession_StopsBeforeRehydrate()
    {
        var client = new FakeRuntimeClient
        {
            ResumeResult = new SessionResumeResult(
                "session",
                RuntimeMode.Expert,
                null,
                null,
                [],
                null,
                0,
                null,
                IsFullyRecoverable: false,
                RecoveryDiagnostics: ["missing definition"]),
        };
        var service = new SessionRecoveryService(client);

        var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            service.ResumeAndRehydrateAsync("session", [], TestContext.CancellationToken));

        StringAssert.Contains(exception.Message, "missing definition");
    }
}
