using Madorin.AI.Runtime.Cli.Commands;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class TokenCountFormatTests
{
    [TestMethod]
    [DataRow(0, "0")]
    [DataRow(999, "999")]
    [DataRow(1_000, "1k")]
    [DataRow(1_700, "1.7k")]
    [DataRow(900_000, "900k")]
    [DataRow(1_000_000, "1m")]
    [DataRow(1_500_000, "1.5m")]
    public void Format_UsesThousandsAndMillions(int count, string expected)
    {
        Assert.AreEqual(expected, TokenCountFormat.Format(count));
    }
}
