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

    [TestMethod]
    [DataRow(0, "0.00k")]
    [DataRow(50, "0.05k")]
    [DataRow(1_234, "1.23k")]
    [DataRow(1_995, "2.00k")]
    [DataRow(1_500_000, "1500.00k")]
    public void FormatKilo_UsesTwoDecimalThousands(int count, string expected)
    {
        Assert.AreEqual(expected, TokenCountFormat.FormatKilo(count));
    }
}
