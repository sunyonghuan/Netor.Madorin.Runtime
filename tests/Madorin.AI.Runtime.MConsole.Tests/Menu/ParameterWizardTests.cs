using Madorin.AI.Runtime.MConsole.Menu;

namespace Madorin.AI.Runtime.MConsole.Tests.Menu;

[TestClass]
public sealed class ParameterWizardTests
{
    private static readonly string[] ExpectedMemoryPrefix = ["memory", "add", "global"];

    [TestMethod]
    public void Build_UsesArgumentArrayGlobalDefaultsAndSeparateStdin()
    {
        var command = CommandCatalog.CreateDefault().Get("memory.add");
        var defaults = new CommandDefaults(
            Environment.CurrentDirectory,
            null,
            "Information",
            NoColor: true,
            Json: true);

        var invocation = ParameterWizard.Build(
            command,
            defaults,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["scope"] = "global",
                ["stdin"] = "remember this",
            });

        CollectionAssert.AreEqual(ExpectedMemoryPrefix, invocation.Arguments.Take(3).ToArray());
        Assert.AreEqual("remember this" + Environment.NewLine, invocation.StandardInput);
        Assert.IsTrue(invocation.Arguments.Contains("--workspace", StringComparer.Ordinal));
        Assert.IsTrue(invocation.Arguments.Contains("--json", StringComparer.Ordinal));
    }

    [TestMethod]
    public async Task CollectAsync_InvalidNumberRepromptsAndUsesValidValue()
    {
        var command = CommandCatalog.CreateDefault().Get("serve");
        var input = new StringReader(Environment.NewLine + Environment.NewLine + "0\n4\n");
        var output = new StringWriter();
        var wizard = new ParameterWizard(input, output);

        var invocation = await wizard.CollectAsync(
            command,
            new CommandDefaults(Environment.CurrentDirectory, null, "Information", true, true));

        Assert.AreEqual("4", invocation.Values["--max-runs"]);
        StringAssert.Contains(output.ToString(), "between 1 and 1024");
    }
}
