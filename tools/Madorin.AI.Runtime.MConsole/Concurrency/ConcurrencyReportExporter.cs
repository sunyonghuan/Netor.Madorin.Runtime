using System.Text;

namespace Madorin.AI.Runtime.MConsole.Concurrency;

public static class ConcurrencyReportExporter
{
    public static async Task ExportMarkdownAsync(
        ConcurrencyScenarioReport report,
        string path,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var builder = new StringBuilder()
            .Append("# Concurrency Report: ").AppendLine(report.Name).AppendLine()
            .Append("Started: ").AppendLine(report.StartedAt.ToString("O"))
            .Append("Completed: ").AppendLine(report.CompletedAt.ToString("O")).AppendLine()
            .AppendLine("## Steps").AppendLine()
            .AppendLine("| Step | Workspace | Exit | Started | Exited |")
            .AppendLine("| --- | --- | ---: | --- | --- |");
        foreach (var step in report.Steps)
        {
            builder.Append("| ").Append(step.StepId).Append(" | ").Append(step.WorkspaceId)
                .Append(" | ").Append(step.Execution.ExitCode).Append(" | ")
                .Append(step.Timeline.StartedAt.ToString("O")).Append(" | ")
                .Append(step.Timeline.ExitedAt.ToString("O")).AppendLine(" |");
        }

        builder.AppendLine().AppendLine("## Assertions").AppendLine();
        foreach (var assertion in report.Assertions)
        {
            builder.Append("- ").Append(assertion.Passed ? "PASS" : "FAIL")
                .Append(' ').Append(assertion.Kind).Append(": ").AppendLine(assertion.Message);
        }

        await File.WriteAllTextAsync(path, builder.ToString(), Encoding.UTF8, cancellationToken);
    }
}
