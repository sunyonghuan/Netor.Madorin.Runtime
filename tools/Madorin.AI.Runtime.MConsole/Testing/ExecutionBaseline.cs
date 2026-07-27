namespace Madorin.AI.Runtime.MConsole.Testing;

public sealed record BaselineMaskRule(string Pattern, string Replacement = "<masked>");

public sealed record ExecutionBaseline(
    string Name,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    string? DiagnosticId,
    AssertionDefinition[] Assertions,
    BaselineMaskRule[] Masks,
    DateTimeOffset CreatedAt);

public sealed record BaselineDifference(string Field, string Expected, string Actual);
