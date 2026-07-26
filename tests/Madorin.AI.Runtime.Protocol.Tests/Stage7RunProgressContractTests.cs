using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Protocol.Tests;

[TestClass]
public sealed class Stage7RunProgressContractTests
{
    private static readonly DateTimeOffset UpdatedAt =
        new(2026, 7, 26, 2, 3, 4, TimeSpan.Zero);

    [TestMethod]
    public void RunQueryResult_OldJsonDefaultsNewFieldsToNull()
    {
        const string json = "{\"runId\":\"run-old\",\"sessionId\":\"session-old\",\"status\":\"Completed\",\"terminalText\":\"done\"}";
        var result = JsonSerializer.Deserialize(
            json,
            RuntimeJsonContext.Default.RunQueryResult);

        Assert.IsNotNull(result);
        Assert.IsNull(result.Mode);
        Assert.IsNull(result.Phase);
        Assert.IsNull(result.LastRunSequence);
        Assert.IsNull(result.Error);
    }

    [TestMethod]
    public void RunProgressContracts_MatchVersionedSnapshotsAndRoundTrip()
    {
        AssertSnapshot(
            "run-query-result-progress.v1.json",
            new RunQueryResult("run-query", "session-1", RunStatus.Running, null)
            {
                Mode = RuntimeMode.Work,
                Phase = "waiting-for-tool",
                CurrentStep = "step-2",
                CompletedUnits = 1,
                TotalUnits = 3,
                UpdatedAt = UpdatedAt,
                LastRunSequence = 7,
                Error = CreateError()
            },
            RuntimeJsonContext.Default.RunQueryResult);

        AssertSnapshot(
            "run-list-item-progress.v1.json",
            new RunListItem("run-list", "session-2", RunStatus.WaitingForApproval, UpdatedAt.AddHours(-1))
            {
                Mode = RuntimeMode.Meeting,
                Phase = "waiting-for-approval",
                CurrentStep = "round:1:approval",
                CompletedUnits = 0,
                TotalUnits = 2,
                UpdatedAt = UpdatedAt,
                LastRunSequence = 4
            },
            RuntimeJsonContext.Default.RunListItem);

        AssertSnapshot(
            "run-status-changed-progress.v1.json",
            new RunStatusChangedEvent("run-event", RunStatus.Persisting.ToString())
            {
                Mode = RuntimeMode.Expert,
                Phase = "persisting",
                CurrentStep = "expert:finalizing",
                CompletedUnits = 8,
                TotalUnits = 8,
                UpdatedAt = UpdatedAt
            },
            RuntimeJsonContext.Default.RunStatusChangedEvent);
    }

    [TestMethod]
    public void RunProgressContracts_IgnoreUnknownFields()
    {
        const string json = "{\"runId\":\"run-unknown\",\"sessionId\":\"session-unknown\",\"status\":\"Running\",\"terminalText\":null,\"futureField\":true}";
        var result = JsonSerializer.Deserialize(
            json,
            RuntimeJsonContext.Default.RunQueryResult);

        Assert.IsNotNull(result);
        Assert.AreEqual("run-unknown", result.RunId);
        Assert.IsNull(result.Mode);
    }

    private static RuntimeError CreateError() =>
        new(
            RuntimeErrorCodes.ToolExecutionFailed,
            "tool",
            "Tool execution failed.",
            IsRetryable: true,
            ProviderDetails: null,
            DiagnosticId: "diag-progress-1");

    private static void AssertSnapshot<T>(
        string fileName,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        var snapshotPath = Path.Combine(AppContext.BaseDirectory, "Snapshots", fileName);
        using var expected = JsonDocument.Parse(File.ReadAllText(snapshotPath));
        Assert.AreEqual("madorin.ai.runtime.protocol-snapshot/v1", expected.RootElement.GetProperty("$schema").GetString());
        Assert.AreEqual("1.1", expected.RootElement.GetProperty("protocolVersion").GetString());
        var actual = JsonSerializer.SerializeToElement(value, typeInfo);
        Assert.IsTrue(
            JsonElement.DeepEquals(expected.RootElement.GetProperty("payload"), actual),
            $"Snapshot mismatch for {fileName}: {actual}");

        var roundTrip = JsonSerializer.Deserialize(actual.GetRawText(), typeInfo);
        Assert.IsNotNull(roundTrip);
        var roundTripped = JsonSerializer.SerializeToElement(roundTrip, typeInfo);
        Assert.IsTrue(JsonElement.DeepEquals(actual, roundTripped));
    }
}
