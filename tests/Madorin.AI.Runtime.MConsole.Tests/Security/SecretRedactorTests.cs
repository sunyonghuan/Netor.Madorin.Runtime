using Madorin.AI.Runtime.MConsole.Security;

namespace Madorin.AI.Runtime.MConsole.Tests.Security;

[TestClass]
public sealed class SecretRedactorTests
{
    private static readonly string[] ExpectedRedactedArguments =
        ["ctl", "--secret=<redacted>", "--api-key", "<redacted>", "--run", "r1"];

    [TestMethod]
    public void RedactArguments_InlineAndFollowingValues_RedactsBothForms()
    {
        string[] arguments = ["ctl", "--secret=value", "--api-key", "other", "--run", "r1"];

        var redacted = SecretRedactor.RedactArguments(arguments);

        CollectionAssert.AreEqual(ExpectedRedactedArguments, redacted);
    }

    [TestMethod]
    public void RedactText_KnownSecret_ReplacesEveryOccurrence()
    {
        var redacted = SecretRedactor.RedactText("before abc after abc", ["abc"]);

        Assert.AreEqual("before <redacted> after <redacted>", redacted);
    }
}
