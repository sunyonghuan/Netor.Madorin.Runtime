using System.Text;
using System.Text.Json;

using Madorin.AI.Runtime.MConsole.Serialization;

namespace Madorin.AI.Runtime.MConsole.Testing;

public static class TestCaseReportExporter
{
    public static async Task ExportJsonAsync(
        TestCaseReport report,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(
            stream,
            report,
            MConsoleJsonContext.Persistence.TestCaseReport,
            cancellationToken);
    }

    public static async Task ExportMarkdownAsync(
        TestCaseReport report,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder()
            .Append("# ").AppendLine(report.Name).AppendLine()
            .Append("- Result: ").AppendLine(report.IsSuccess ? "Passed" : "Failed")
            .Append("- Passed: ").AppendLine(report.Passed.ToString())
            .Append("- Failed: ").AppendLine(report.Failed.ToString())
            .Append("- Skipped: ").AppendLine(report.Skipped.ToString()).AppendLine()
            .AppendLine("| Step | Result | Exit | Error |")
            .AppendLine("| --- | --- | ---: | --- |");
        foreach (var step in report.Steps)
        {
            builder.Append("| ").Append(step.StepId)
                .Append(" | ").Append(step.Passed ? "Passed" : "Failed")
                .Append(" | ").Append(step.ExitCode?.ToString() ?? string.Empty)
                .Append(" | ").Append((step.Error ?? string.Empty).Replace("|", "\\|", StringComparison.Ordinal))
                .AppendLine(" |");
        }

        if (report.FixtureCleanupError is not null)
        {
            builder.AppendLine().Append("Cleanup error: ").AppendLine(report.FixtureCleanupError);
        }

        await File.WriteAllTextAsync(path, builder.ToString(), Encoding.UTF8, cancellationToken);
    }
}
