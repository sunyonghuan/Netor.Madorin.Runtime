using System.Text.Json;

using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.MConsole.HostClient;
using Madorin.AI.Runtime.MConsole.Tests.TestSupport;

namespace Madorin.AI.Runtime.MConsole.Tests.HostClient;

[TestClass]
public sealed class EventStreamConsumerTests
{
    private static readonly string[] ExpectedProcessingOrder =
        ["project:1", "ack:1", "project:2", "ack:2"];

    [TestMethod]
    public async Task ConsumeUntilRunTerminalAsync_ValidEvents_ProjectsBeforeAcknowledging()
    {
        using var directory = new OwnedTestDirectory();
        using var cursorStore = new RuntimeCursorStore(directory.Path);
        var client = new FakeRuntimeClient
        {
            Events =
            [
                CreateEvent(1, MessageTypes.RunAccepted),
                CreateEvent(2, MessageTypes.RunCompleted),
            ],
        };
        var projector = new RecordingProjector(client.Operations);
        var consumer = new EventStreamConsumer(client, cursorStore, projector);

        var result = await consumer.ConsumeUntilRunTerminalAsync("runtime-a", "run-a");

        CollectionAssert.AreEqual(ExpectedProcessingOrder, client.Operations);
        Assert.AreEqual(2, result.LastConfirmedGsn);
        Assert.AreEqual(MessageTypes.RunCompleted, result.TerminalMessageType);
        Assert.AreEqual(2, (await cursorStore.GetAsync("runtime-a")).LastConfirmedGsn);
    }

    [TestMethod]
    public async Task ConsumeUntilRunTerminalAsync_ProjectorFails_DoesNotAcknowledgeOrAdvance()
    {
        using var directory = new OwnedTestDirectory();
        using var cursorStore = new RuntimeCursorStore(directory.Path);
        var client = new FakeRuntimeClient { Events = [CreateEvent(1, MessageTypes.RunAccepted)] };
        var consumer = new EventStreamConsumer(client, cursorStore, new ThrowingProjector());

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => consumer.ConsumeUntilRunTerminalAsync("runtime-a", "run-a"));

        Assert.IsEmpty(client.Operations);
        Assert.AreEqual(0, (await cursorStore.GetAsync("runtime-a")).LastConfirmedGsn);
    }

    private static RuntimeEventEnvelope CreateEvent(long gsn, string messageType)
    {
        using var document = JsonDocument.Parse("{}");
        return new RuntimeEventEnvelope(
            "runtime-a",
            gsn,
            "run-a",
            gsn,
            messageType,
            DateTimeOffset.UtcNow,
            document.RootElement.Clone());
    }

    private sealed class RecordingProjector(List<string> operations) : IHostEventProjector
    {
        public ValueTask ProjectAsync(
            RuntimeEventEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            operations.Add($"project:{envelope.Gsn}");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingProjector : IHostEventProjector
    {
        public ValueTask ProjectAsync(
            RuntimeEventEnvelope envelope,
            CancellationToken cancellationToken = default) =>
            throw new InvalidDataException("projection failed");
    }
}
