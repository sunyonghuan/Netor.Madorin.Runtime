using System.Text.Json;
using System.Text.RegularExpressions;

using Madorin.AI.Runtime.MConsole.Hosting;
using Madorin.AI.Runtime.MConsole.Serialization;

namespace Madorin.AI.Runtime.MConsole.Testing;

public sealed class BaselineStore(string baselineDirectory)
{
    private static readonly BaselineMaskRule[] DefaultMasks =
    [
        new("\\bpid[=: ]+\\d+", "pid=<masked>"),
        new("diagnostic[-_ ]?id[=: ]+[A-Za-z0-9_-]+", "diagnosticId=<masked>"),
        new("\\b\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}(?:\\.\\d+)?(?:Z|[+-]\\d{2}:\\d{2})\\b", "<timestamp>"),
    ];

    private readonly string _baselineDirectory = Path.GetFullPath(baselineDirectory);

    public async Task<ExecutionBaseline> SaveAsync(
        string name,
        ExecutionResult execution,
        IEnumerable<AssertionDefinition> assertions,
        IEnumerable<BaselineMaskRule>? masks = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(execution);
        var allMasks = DefaultMasks.Concat(masks ?? []).ToArray();
        var baseline = new ExecutionBaseline(
            name,
            execution.ExitCode,
            ApplyMasks(execution.StandardOutput, allMasks),
            ApplyMasks(execution.StandardError, allMasks),
            execution.Envelope?.DiagnosticId is null ? null : "<masked>",
            assertions.ToArray(),
            allMasks,
            DateTimeOffset.UtcNow);
        Directory.CreateDirectory(_baselineDirectory);
        var path = GetPath(name);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    baseline,
                    MConsoleJsonContext.Persistence.ExecutionBaseline,
                    cancellationToken);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return baseline;
    }

    public async Task<ExecutionBaseline> LoadAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(GetPath(name));
        return await JsonSerializer.DeserializeAsync(
                stream,
                MConsoleJsonContext.Default.ExecutionBaseline,
                cancellationToken)
            ?? throw new InvalidDataException($"Baseline '{name}' is empty.");
    }

    public static IReadOnlyList<BaselineDifference> Compare(
        ExecutionBaseline baseline,
        ExecutionResult actual)
    {
        var differences = new List<BaselineDifference>();
        AddDifference(differences, "exitCode", baseline.ExitCode.ToString(), actual.ExitCode.ToString());
        AddDifference(
            differences,
            "stdout",
            baseline.StandardOutput,
            ApplyMasks(actual.StandardOutput, baseline.Masks));
        AddDifference(
            differences,
            "stderr",
            baseline.StandardError,
            ApplyMasks(actual.StandardError, baseline.Masks));
        return differences;
    }

    private string GetPath(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        if (name.Any(invalid.Contains))
        {
            throw new ArgumentException("Baseline name contains invalid path characters.", nameof(name));
        }

        return Path.Combine(_baselineDirectory, name + ".json");
    }

    private static string ApplyMasks(string value, IEnumerable<BaselineMaskRule> masks)
    {
        var result = value;
        foreach (var mask in masks)
        {
            result = Regex.Replace(
                result,
                mask.Pattern,
                mask.Replacement,
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(1));
        }

        return result;
    }

    private static void AddDifference(
        List<BaselineDifference> result,
        string field,
        string expected,
        string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            result.Add(new BaselineDifference(field, expected, actual));
        }
    }
}
