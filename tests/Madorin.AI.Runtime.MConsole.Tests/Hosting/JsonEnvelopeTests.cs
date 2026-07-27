using Madorin.AI.Runtime.MConsole.Hosting;

namespace Madorin.AI.Runtime.MConsole.Tests.Hosting;

[TestClass]
public sealed class JsonEnvelopeTests
{
    [TestMethod]
    public void TryParse_ValidEnvelope_ReturnsClonedFields()
    {
        const string json = """
            {"success":true,"data":{"version":"1.0"},"warnings":["notice"],"diagnosticId":"d-1"}
            """;

        var parsed = JsonEnvelope.TryParse(json, out var envelope, out var error);

        Assert.IsTrue(parsed, error);
        Assert.IsNotNull(envelope);
        Assert.IsTrue(envelope.Success);
        Assert.AreEqual("1.0", envelope.Data?.GetProperty("version").GetString());
        Assert.HasCount(1, envelope.Warnings);
        Assert.AreEqual("notice", envelope.Warnings[0]);
        Assert.AreEqual("d-1", envelope.DiagnosticId);
    }

    [TestMethod]
    public void TryParse_NonJsonOutput_ReturnsFalseWithoutThrowing()
    {
        var parsed = JsonEnvelope.TryParse("plain text", out var envelope, out var error);

        Assert.IsFalse(parsed);
        Assert.IsNull(envelope);
        Assert.IsNotNull(error);
    }
}
