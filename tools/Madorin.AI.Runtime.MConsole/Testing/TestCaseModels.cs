using System.Text.Json;

namespace Madorin.AI.Runtime.MConsole.Testing;

public enum TestStepKind
{
    Command,
    Host,
}

public sealed record TestStepDefinition(
    string Id,
    TestStepKind Kind,
    string? CommandId,
    string? HostAction,
    Dictionary<string, string?> Arguments,
    AssertionDefinition[] Assertions);

public sealed record TestFixtureDefinition(string? Before = null, string? After = null);

public sealed record MConsoleTestCase(
    string Name,
    TestStepDefinition[] Steps,
    TestFixtureDefinition Fixtures,
    bool StopOnFailure = true,
    bool AllowDangerousOperations = false);

public sealed record TestStepReport(
    string StepId,
    bool Passed,
    int? ExitCode,
    AssertionResult[] Assertions,
    string? Error,
    JsonElement? Data);

public sealed record TestCaseReport(
    string Name,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int Passed,
    int Failed,
    int Skipped,
    TestStepReport[] Steps,
    string? FixtureCleanupError)
{
    public bool IsSuccess => Failed == 0 && FixtureCleanupError is null;
}

public sealed record HostStepResult(bool Success, JsonElement? Data, string? Error = null);

public interface IHostTestActionExecutor
{
    public Task<HostStepResult> ExecuteAsync(
        string action,
        IReadOnlyDictionary<string, string?> arguments,
        CancellationToken cancellationToken = default);
}
