using Madorin.AI.Runtime.MConsole.Menu;

namespace Madorin.AI.Runtime.MConsole.Tests.Menu;

[TestClass]
public sealed class CommandInvocationValidatorTests
{
    public static IEnumerable<object[]> InvalidCases
    {
        get
        {
            yield return ["run", Values(("--input", "a"), ("--input-file", "b")), "conflicts"];
            yield return ["run", Values(("--output-file", "out")), "requires"];
            yield return ["doctor", Values(("--yes", "true")), "requires --fix"];
            yield return ["storage.gc", Values(("--dry-run", "true"), ("--confirm", "true")), "conflicts"];
            yield return ["session.delete", Values(("session-id", "s")), "requires --confirm"];
            yield return ["ctl.credential.update", Values(("--run", "r")), "required"];
        }
    }

    [TestMethod]
    [DynamicData(nameof(InvalidCases))]
    public void Validate_KnownInvalidCombination_IsRejected(
        string commandId,
        Dictionary<string, string?> values,
        string expectedMessage)
    {
        var command = CommandCatalog.CreateDefault().Get(commandId);
        var defaults = new CommandDefaults(Environment.CurrentDirectory, null, "Information", true, true);
        var invocation = ParameterWizard.Build(command, defaults, values);

        var result = CommandInvocationValidator.Validate(invocation, defaults);

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Errors.Any(error => error.Contains(
            expectedMessage,
            StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void Validate_ServeWithoutWorkspace_IsRejected()
    {
        var command = CommandCatalog.CreateDefault().Get("serve");
        var defaults = new CommandDefaults(null, null, "Information", true, true);
        var invocation = ParameterWizard.Build(command, defaults, Values());

        var result = CommandInvocationValidator.Validate(invocation, defaults);

        Assert.IsFalse(result.IsValid);
        StringAssert.Contains(result.Errors.Single(), "--workspace");
    }

    private static Dictionary<string, string?> Values(
        params (string Name, string? Value)[] values) =>
        values.ToDictionary(static item => item.Name, static item => item.Value, StringComparer.Ordinal);
}
