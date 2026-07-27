using System.Text.Json;

using Madorin.AI.Runtime.MConsole.Callbacks;
using Madorin.AI.Runtime.MConsole.HostClient;

namespace Madorin.AI.Runtime.MConsole.Testing;

public enum AssertionKind
{
    ExitCodeEquals,
    ExitCodeIn,
    JsonSuccess,
    JsonPathExists,
    JsonPathEquals,
    StdoutContains,
    StderrContains,
    StdoutRegex,
    StderrRegex,
    NoAnsi,
    DurationLessThan,
    NoDataChange,
    HostEventAppears,
    HostGsnMonotonic,
    HostCallbackIdempotent,
    HostRunTerminalState,
    HostBindingMatches,
}

public sealed record AssertionDefinition(
    AssertionKind Kind,
    string? Path = null,
    string? Expected = null,
    string[]? ExpectedAny = null,
    long? MaximumMilliseconds = null,
    string? Source = null,
    bool Enabled = true);

public sealed record AssertionResult(
    AssertionKind Kind,
    bool Passed,
    string? Expected,
    string? Actual,
    string Message);

public sealed record HostAssertionContext(
    IReadOnlyList<HostEventProjection> Events,
    IReadOnlyList<RunProjection> Runs,
    IReadOnlyList<HostInteractionResult> Interactions,
    HostBindingSnapshot? Binding);
