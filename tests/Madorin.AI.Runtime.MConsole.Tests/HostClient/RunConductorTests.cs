using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.MConsole.HostClient;
using Madorin.AI.Runtime.MConsole.Tests.TestSupport;

namespace Madorin.AI.Runtime.MConsole.Tests.HostClient;

[TestClass]
public sealed class RunConductorTests
{
    private static readonly RunRequestDraft Draft = new(
        RuntimeMode.Expert,
        "hello",
        new RunAgentDraft("agent", "provider", "model"));

    [TestMethod]
    public async Task StartNewSessionAsync_ValidDraft_ReturnsBoundSubmission()
    {
        var client = new FakeRuntimeClient();
        var conductor = new RunConductor(client, "runtime-a");

        var submission = await conductor.StartNewSessionAsync(Draft);

        Assert.AreEqual("run-new", submission.RunId);
        Assert.AreEqual("runtime-a", submission.RuntimeInstanceId);
        Assert.IsFalse(submission.IsExistingSession);
        Assert.IsNotNull(client.LastNewRequest);
    }

    [TestMethod]
    public async Task StartExistingSessionAsync_ValidDraft_PreservesSessionId()
    {
        var client = new FakeRuntimeClient();
        var conductor = new RunConductor(client, "runtime-a");

        var submission = await conductor.StartExistingSessionAsync("session-a", Draft, 1);

        Assert.AreEqual("run-existing", submission.RunId);
        Assert.AreEqual("session-a", submission.SessionId);
        Assert.IsTrue(submission.IsExistingSession);
        Assert.AreEqual(1, client.LastExistingRequest?.ExpectedSelectionVersion);
    }

    [TestMethod]
    public async Task CancelAsync_AcceptedButRunning_DoesNotReportTerminal()
    {
        var client = new FakeRuntimeClient
        {
            CancelAccepted = true,
            QueryResult = new("run", "session", RunStatus.Running, null),
        };
        var conductor = new RunConductor(client, "runtime-a");

        var outcome = await conductor.CancelAsync("run");

        Assert.IsTrue(outcome.Accepted);
        Assert.AreEqual(RunStatus.Running, outcome.ObservedStatus);
        Assert.IsFalse(outcome.IsTerminal);
    }
}
