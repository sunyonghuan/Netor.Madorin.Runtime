using System.Text;
using System.Text.Json;

using Madorin.AI.Runtime.MConsole.Hosting;
using Madorin.AI.Runtime.MConsole.Security;
using Madorin.AI.Runtime.MConsole.Serialization;

namespace Madorin.AI.Runtime.MConsole.Testing;

public sealed class ExecutionHistoryStore(string historyDirectory) : IDisposable
{
    private static readonly MConsoleJsonContext CompactJsonContext = new(
        new JsonSerializerOptions(MConsoleJsonContext.Persistence.Options)
        {
            WriteIndented = false,
        });

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _historyDirectory = Path.GetFullPath(historyDirectory);

    public async Task<ExecutionHistoryEntry> AppendExecutionAsync(
        string commandId,
        ExecutionResult execution,
        IReadOnlyList<AssertionResult> assertions,
        string? workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(assertions);
        var id = Guid.NewGuid().ToString("N");
        var entry = new ExecutionHistoryEntry(
            id,
            "command",
            commandId,
            SecretRedactor.RedactArguments(execution.Arguments),
            execution.StartedAt,
            (long)execution.Duration.TotalMilliseconds,
            execution.ExitCode,
            assertions.Count(static item => item.Passed),
            assertions.Count(static item => !item.Passed),
            workspace,
            null,
            null,
            execution.Envelope?.DiagnosticId,
            null);
        await AppendAsync(entry, cancellationToken);
        return entry;
    }

    public async Task AppendAsync(
        ExecutionHistoryEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Directory.CreateDirectory(_historyDirectory);
        var path = Path.Combine(_historyDirectory, "history.jsonl");
        var line = JsonSerializer.Serialize(
            entry,
            CompactJsonContext.ExecutionHistoryEntry);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(
                path,
                line + Environment.NewLine,
                Encoding.UTF8,
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ExecutionHistoryEntry>> QueryAsync(
        ExecutionHistoryFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        filter ??= new ExecutionHistoryFilter();
        var path = Path.Combine(_historyDirectory, "history.jsonl");
        if (!File.Exists(path))
        {
            return [];
        }

        var result = new List<ExecutionHistoryEntry>();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await foreach (var line in File.ReadLinesAsync(path, cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var entry = JsonSerializer.Deserialize(
                        line,
                        MConsoleJsonContext.Default.ExecutionHistoryEntry)
                    ?? throw new InvalidDataException("History contains an empty JSON line.");
                if (Matches(entry, filter))
                {
                    result.Add(entry);
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        return result.OrderByDescending(static item => item.StartedAt).ToArray();
    }

    public async Task ExportMarkdownAsync(
        string path,
        ExecutionHistoryFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        var entries = await QueryAsync(filter, cancellationToken);
        var builder = new StringBuilder("# MConsole Execution History\n\n");
        builder.AppendLine("| Time | Kind | Action | Exit | Passed | Failed |");
        builder.AppendLine("| --- | --- | --- | ---: | ---: | ---: |");
        foreach (var entry in entries)
        {
            builder.Append("| ").Append(entry.StartedAt.ToString("O"))
                .Append(" | ").Append(entry.Kind)
                .Append(" | ").Append(entry.CommandOrHostAction.Replace("|", "\\|", StringComparison.Ordinal))
                .Append(" | ").Append(entry.ExitCode?.ToString() ?? string.Empty)
                .Append(" | ").Append(entry.AssertionPassed)
                .Append(" | ").Append(entry.AssertionFailed).AppendLine(" |");
        }

        await File.WriteAllTextAsync(path, builder.ToString(), Encoding.UTF8, cancellationToken);
    }

    public void Dispose() => _gate.Dispose();

    private static bool Matches(ExecutionHistoryEntry entry, ExecutionHistoryFilter filter) =>
        (filter.CommandPrefix is null
            || entry.CommandOrHostAction.StartsWith(filter.CommandPrefix, StringComparison.Ordinal))
        && (filter.Passed is null
            || filter.Passed.Value == (entry.AssertionFailed == 0))
        && (filter.From is null || entry.StartedAt >= filter.From.Value)
        && (filter.To is null || entry.StartedAt <= filter.To.Value);
}
