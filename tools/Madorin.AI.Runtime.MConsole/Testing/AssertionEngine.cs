using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.MConsole.Hosting;

namespace Madorin.AI.Runtime.MConsole.Testing;

public static partial class AssertionEngine
{
    public static IReadOnlyList<AssertionResult> Evaluate(
        ExecutionResult execution,
        IEnumerable<AssertionDefinition> definitions,
        FileTreeSnapshot? before = null,
        FileTreeSnapshot? after = null) => definitions
        .Where(static item => item.Enabled)
        .Select(definition => EvaluateExecution(execution, definition, before, after))
        .ToArray();

    public static IReadOnlyList<AssertionResult> Evaluate(
        HostAssertionContext context,
        IEnumerable<AssertionDefinition> definitions) => definitions
        .Where(static item => item.Enabled)
        .Select(definition => EvaluateHost(context, definition))
        .ToArray();

    private static AssertionResult EvaluateExecution(
        ExecutionResult execution,
        AssertionDefinition definition,
        FileTreeSnapshot? before,
        FileTreeSnapshot? after)
    {
        try
        {
            return definition.Kind switch
            {
                AssertionKind.ExitCodeEquals => Compare(
                    definition,
                    definition.Expected,
                    execution.ExitCode.ToString(CultureInfo.InvariantCulture)),
                AssertionKind.ExitCodeIn => InSet(
                    definition,
                    execution.ExitCode.ToString(CultureInfo.InvariantCulture)),
                AssertionKind.JsonSuccess => Compare(
                    definition,
                    definition.Expected ?? "true",
                    execution.Envelope?.Success.ToString(CultureInfo.InvariantCulture).ToLowerInvariant()),
                AssertionKind.JsonPathExists => JsonPathExists(execution, definition),
                AssertionKind.JsonPathEquals => JsonPathEquals(execution, definition),
                AssertionKind.StdoutContains => Contains(definition, execution.StandardOutput),
                AssertionKind.StderrContains => Contains(definition, execution.StandardError),
                AssertionKind.StdoutRegex => RegexMatch(definition, execution.StandardOutput),
                AssertionKind.StderrRegex => RegexMatch(definition, execution.StandardError),
                AssertionKind.NoAnsi => NoAnsi(definition, execution),
                AssertionKind.DurationLessThan => Duration(definition, execution),
                AssertionKind.NoDataChange => NoDataChange(definition, before, after),
                _ => Failed(definition, "ExecutionResult does not support this host assertion."),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException)
        {
            return Failed(definition, ex.Message);
        }
    }

    private static AssertionResult EvaluateHost(
        HostAssertionContext context,
        AssertionDefinition definition)
    {
        try
        {
            return definition.Kind switch
            {
                AssertionKind.HostEventAppears => Boolean(
                    definition,
                    context.Events.Any(item => string.Equals(
                        item.MessageType,
                        definition.Expected,
                        StringComparison.Ordinal)),
                    string.Join(',', context.Events.Select(static item => item.MessageType))),
                AssertionKind.HostGsnMonotonic => Boolean(
                    definition,
                    IsStrictlyIncreasing(context.Events.Select(static item => item.Gsn)),
                    string.Join(',', context.Events.Select(static item => item.Gsn))),
                AssertionKind.HostCallbackIdempotent => Boolean(
                    definition,
                    context.Interactions
                        .Where(item => item.Kind is Callbacks.HostCallbackKind.ToolCall)
                        .GroupBy(static item => (item.RuntimeInstanceId, item.CallId))
                        .All(static group => group.Select(item => item.Outcome).Distinct().Count() == 1),
                    context.Interactions.Count.ToString(CultureInfo.InvariantCulture)),
                AssertionKind.HostRunTerminalState => Boolean(
                    definition,
                    context.Runs.Any(item => string.Equals(
                        item.TerminalMessageType,
                        definition.Expected,
                        StringComparison.Ordinal)),
                    string.Join(',', context.Runs.Select(static item => item.TerminalMessageType))),
                AssertionKind.HostBindingMatches => Boolean(
                    definition,
                    context.Binding is not null
                    && string.Equals(
                        context.Binding.RuntimeInstanceId,
                        definition.Expected,
                        StringComparison.Ordinal),
                    context.Binding?.RuntimeInstanceId),
                _ => Failed(definition, "Host context does not support this execution assertion."),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Failed(definition, ex.Message);
        }
    }

    private static AssertionResult JsonPathExists(
        ExecutionResult execution,
        AssertionDefinition definition)
    {
        var exists = TryResolveJsonPath(execution, definition.Path, out var actual);
        return Boolean(definition, exists, actual?.GetRawText());
    }

    private static AssertionResult JsonPathEquals(
        ExecutionResult execution,
        AssertionDefinition definition)
    {
        if (!TryResolveJsonPath(execution, definition.Path, out var actual))
        {
            return Failed(definition, $"JSON path '{definition.Path}' does not exist.");
        }

        var actualText = actual!.Value.ValueKind is JsonValueKind.String
            ? actual.Value.GetString()
            : actual.Value.GetRawText();
        return Compare(definition, definition.Expected, actualText);
    }

    private static bool TryResolveJsonPath(
        ExecutionResult execution,
        string? path,
        out JsonElement? value)
    {
        value = null;
        if (execution.Envelope is null || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        using var document = JsonDocument.Parse(execution.StandardOutput);
        var current = document.RootElement;
        foreach (var segment in path.TrimStart('$', '.').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind is not JsonValueKind.Object
                || !current.TryGetProperty(segment, out current))
            {
                return false;
            }
        }

        value = current.Clone();
        return true;
    }

    private static AssertionResult Contains(AssertionDefinition definition, string actual) =>
        Boolean(
            definition,
            actual.Contains(definition.Expected ?? string.Empty, StringComparison.Ordinal),
            actual);

    private static AssertionResult RegexMatch(AssertionDefinition definition, string actual) =>
        Boolean(
            definition,
            Regex.IsMatch(actual, definition.Expected ?? string.Empty, RegexOptions.CultureInvariant),
            actual);

    private static AssertionResult NoAnsi(
        AssertionDefinition definition,
        ExecutionResult execution) => Boolean(
            definition,
            !AnsiRegex().IsMatch(execution.StandardOutput)
            && !AnsiRegex().IsMatch(execution.StandardError),
            "stdout/stderr");

    private static AssertionResult Duration(
        AssertionDefinition definition,
        ExecutionResult execution)
    {
        var maximum = definition.MaximumMilliseconds
            ?? throw new ArgumentException("durationLessThan requires MaximumMilliseconds.");
        return Boolean(
            definition,
            execution.Duration.TotalMilliseconds < maximum,
            execution.Duration.TotalMilliseconds.ToString(CultureInfo.InvariantCulture));
    }

    private static AssertionResult NoDataChange(
        AssertionDefinition definition,
        FileTreeSnapshot? before,
        FileTreeSnapshot? after)
    {
        if (before is null || after is null)
        {
            return Failed(definition, "noDataChange requires before and after snapshots.");
        }

        var difference = FileTreeSnapshotService.Compare(before, after);
        return Boolean(
            definition,
            !difference.HasChanges,
            $"added={string.Join(',', difference.Added)}; " +
            $"removed={string.Join(',', difference.Removed)}; " +
            $"modified={string.Join(',', difference.Modified)}");
    }

    private static AssertionResult Compare(
        AssertionDefinition definition,
        string? expected,
        string? actual) => Boolean(
            definition,
            string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase),
            actual,
            expected);

    private static AssertionResult InSet(AssertionDefinition definition, string actual) =>
        Boolean(
            definition,
            definition.ExpectedAny?.Contains(actual, StringComparer.OrdinalIgnoreCase) is true,
            actual,
            definition.ExpectedAny is null ? null : string.Join(',', definition.ExpectedAny));

    private static AssertionResult Boolean(
        AssertionDefinition definition,
        bool passed,
        string? actual,
        string? expected = null) => new(
            definition.Kind,
            passed,
            expected ?? definition.Expected,
            actual,
            passed ? "Assertion passed." : "Assertion failed.");

    private static AssertionResult Failed(AssertionDefinition definition, string message) => new(
        definition.Kind,
        false,
        definition.Expected,
        null,
        message);

    private static bool IsStrictlyIncreasing(IEnumerable<long> values)
    {
        long? previous = null;
        foreach (var value in values)
        {
            if (previous is not null && value <= previous)
            {
                return false;
            }

            previous = value;
        }

        return true;
    }

    [GeneratedRegex("\\x1B\\[[0-?]*[ -/]*[@-~]", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiRegex();
}
