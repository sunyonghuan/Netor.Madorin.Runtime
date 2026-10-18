using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;

namespace Madorin.AI.Runtime.Protocol.Tests;

[TestClass]
public sealed class SessionCompactionContractTests
{
    [TestMethod]
    public void Compaction_LegacyRequestKeepsOptionalFields()
    {
        var request = JsonSerializer.Deserialize("""{"sessionId":"session"}""",
            RuntimeJsonContext.Default.SessionCompactionRequest);
        Assert.IsNotNull(request);
        Assert.AreEqual("summary", request.Strategy);
        Assert.IsNull(request.OperationId);
        Assert.IsNull(request.RawTailSize);
    }

    [TestMethod]
    public void Compaction_RequestRoundTripsOperationAndTail()
    {
        var expected = new SessionCompactionRequest("session", OperationId: "operation", RawTailSize: 8);
        var actual = JsonSerializer.Deserialize(JsonSerializer.Serialize(expected,
            RuntimeJsonContext.Default.SessionCompactionRequest), RuntimeJsonContext.Default.SessionCompactionRequest);
        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void Compaction_StatusRoundTripsWithoutHistoryContent()
    {
        var result = new SessionCompactionResultDto("session", "summary", "provider", "model", null,
            20, 9, 12, 5000, 2000, "provider.estimated", false, false, true,
            Outcome: "Applied", OperationId: "operation", ProjectionRevision: "revision", CoveredThroughSequence: 12);
        var status = new SessionContextStatusDto("session", "operation", "Succeeded", "revision", 12,
            2000, null, 128000, "provider.estimated", true, "full_context_material_unavailable", DateTimeOffset.UtcNow, result);
        var json = JsonSerializer.Serialize(status, RuntimeJsonContext.Default.SessionContextStatusDto);
        var actual = JsonSerializer.Deserialize(json, RuntimeJsonContext.Default.SessionContextStatusDto);
        Assert.AreEqual(status, actual);
        Assert.IsFalse(json.Contains("projectionMessages", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("summaryText", StringComparison.Ordinal));
        Assert.IsNull(actual!.EffectiveContextEstimatedTokens);
    }
}
