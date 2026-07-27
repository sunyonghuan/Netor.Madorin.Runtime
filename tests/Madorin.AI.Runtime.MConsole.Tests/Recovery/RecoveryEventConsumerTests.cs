using System.Text.Json;

using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.MConsole.HostClient;
using Madorin.AI.Runtime.MConsole.Recovery;
using Madorin.AI.Runtime.MConsole.Tests.TestSupport;

namespace Madorin.AI.Runtime.MConsole.Tests.Recovery;

[TestClass]
public sealed class RecoveryEventConsumerTests
{
    private static readonly long[] ExpectedGsns = [1, 2];
    private static readonly string[] ExpectedTwoAcknowledgements = ["ack:1", "ack:2"];
    private static readonly string[] ExpectedOneAcknowledgement = ["ack:1"];

    public TestContext TestContext { get; set; }

    [TestMethod]
    public async Task RecoverUntilRunTerminalAsync_DeduplicatesBeforeProjectionAndConfirmation()
    {
        using var directory = new OwnedTestDirectory();
        using var cursors = new RuntimeCursorStore(directory.Path);
        var client = new FakeRuntimeClient
        {
            Events =
            [
                CreateEvent(1, MessageTypes.RunAccepted),
                CreateEvent(1, MessageTypes.RunAccepted),
                CreateEvent(2, MessageTypes.RunCompleted),
            ],
        };
        var projector = new RecordingProjector();
        var consumer = new RecoveryEventConsumer(client, cursors, projector);

        var result = await consumer.RecoverUntilRunTerminalAsync(
            "runtime",
            "run",
            TestContext.CancellationToken);

        Assert.AreEqual(2, result.ProjectedCount);
        Assert.AreEqual(1, result.DuplicateCount);
        CollectionAssert.AreEqual(ExpectedGsns, projector.Gsns.ToArray());
        CollectionAssert.AreEqual(ExpectedTwoAcknowledgements, client.Operations);
        Assert.AreEqual(
            2,
            (await cursors.GetAsync("runtime", TestContext.CancellationToken)).LastConfirmedGsn);
    }

    [TestMethod]
    public async Task DemonstrateEarlyAcknowledgementLoss_DoesNotPolluteNormalCursor()
    {
        using var directory = new OwnedTestDirectory();
        using var cursors = new RuntimeCursorStore(directory.Path);
        var client = new FakeRuntimeClient { Events = [CreateEvent(1, MessageTypes.RunAccepted)] };
        var consumer = new RecoveryEventConsumer(client, cursors, new RecordingProjector());

        var exception = await Assert.ThrowsExactlyAsync<EarlyAcknowledgementDemonstratedException>(
            () => consumer.DemonstrateEarlyAcknowledgementLossAsync(
                "runtime",
                TestContext.CancellationToken));

        Assert.AreEqual(1, exception.AcknowledgedGsn);
        CollectionAssert.AreEqual(ExpectedOneAcknowledgement, client.Operations);
        Assert.AreEqual(
            0,
            (await cursors.GetAsync("runtime", TestContext.CancellationToken)).LastConfirmedGsn);
    }

    private static RuntimeEventEnvelope CreateEvent(long gsn, string messageType)
    {
        using var document = JsonDocument.Parse("{}");
        return new RuntimeEventEnvelope(
            "runtime",
            gsn,
            "run",
            gsn,
            messageType,
            DateTimeOffset.UtcNow,
            document.RootElement.Clone());
    }

    private sealed class RecordingProjector : IHostEventProjector
    {
        public List<long> Gsns { get; } = [];

        public ValueTask ProjectAsync(
            RuntimeEventEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            Gsns.Add(envelope.Gsn);
            return ValueTask.CompletedTask;
        }
    }
}
