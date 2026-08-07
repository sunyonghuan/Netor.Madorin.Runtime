using System.Text.Json;
using Madorin.AI.Runtime.Cli;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class CliCommandSmokeTests
{
    [TestMethod]
    public void Run_DoctorHelp_ReturnsSuccess()
    {
        using var output = new StringWriter();

        var exitCode = CliApplication.Run(["doctor", "--help"], output);

        Assert.AreEqual(ExitCodes.Success, exitCode);
        StringAssert.Contains(output.ToString(), "--fix");
    }

    [TestMethod]
    public void Run_Version_ReturnsSuccess()
    {
        using var output = new StringWriter();

        var exitCode = CliApplication.Run(["version"], output);

        Assert.AreEqual(ExitCodes.Success, exitCode);
    }

    [TestMethod]
    public void Run_ConfigValidate_ReturnsSuccess()
    {
        using var output = new StringWriter();

        var exitCode = CliApplication.Run(["config", "validate"], output);

        Assert.AreEqual(ExitCodes.Success, exitCode);
    }

    [TestMethod]
    public void Run_ProviderProtocols_ListsAllSupportedProtocols()
    {
        using var output = new StringWriter();

        var exitCode = CliApplication.Run(["provider", "protocols"], output);

        Assert.AreEqual(ExitCodes.Success, exitCode);
        Assert.Contains("OpenAI", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Azure OpenAI", output.ToString(), StringComparison.Ordinal);
    }

    [TestMethod]
    public void Run_ProviderProtocolsJson_UsesUnifiedEnvelope()
    {
        using var output = new StringWriter();

        var exitCode = CliApplication.Run(["provider", "protocols", "--json"], output);

        Assert.AreEqual(ExitCodes.Success, exitCode, output.ToString());
        using var document = JsonDocument.Parse(output.ToString());
        Assert.IsTrue(document.RootElement.GetProperty("success").GetBoolean());
        var data = document.RootElement.GetProperty("data");
        Assert.AreEqual("provider protocols", data.GetProperty("command").GetString());
        var protocols = data.GetProperty("protocols").EnumerateArray()
            .Select(static protocol => protocol.GetString())
            .ToArray();
        CollectionAssert.Contains(protocols, "OpenAI");
        CollectionAssert.Contains(protocols, "Azure OpenAI");
    }
}
