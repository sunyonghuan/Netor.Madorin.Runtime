using Madorin.AI.Runtime.MConsole.Menu;

namespace Madorin.AI.Runtime.MConsole.Tests.Menu;

[TestClass]
public sealed class DangerousCommandGuardTests
{
    [TestMethod]
    public async Task ConfirmAsync_RequiresExactCommandId()
    {
        var command = CommandCatalog.CreateDefault().Get("session.delete");
        var defaults = new CommandDefaults(Environment.CurrentDirectory, null, "Information", true, true);
        var invocation = ParameterWizard.Build(
            command,
            defaults,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["session-id"] = "session",
                ["--confirm"] = "true",
            });
        var output = new StringWriter();

        var rejected = await DangerousCommandGuard.ConfirmAsync(
            invocation,
            defaults,
            new StringReader("yes\n"),
            output);
        var accepted = await DangerousCommandGuard.ConfirmAsync(
            invocation,
            defaults,
            new StringReader("session.delete\n"),
            output);

        Assert.IsFalse(rejected);
        Assert.IsTrue(accepted);
    }
}
