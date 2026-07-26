using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Tools.Abstractions;

/// <summary>Describes the normalized minimum permission dimensions requested by one tool call.</summary>
public sealed record ToolRequestedScope(
    string TargetSummary,
    string DetailSummary,
    string[] AllowedReadRoots,
    string[] AllowedWriteRoots,
    bool AllowOverwrite,
    bool AllowMove,
    bool AllowDelete,
    string[] AllowedExecutables,
    string[] AllowedEnvironmentVariables,
    bool AllowPowerShell,
    NetworkPolicy NetworkPolicy,
    bool CanGrantForRun);

/// <summary>Contains either a safely normalized tool scope or a fail-closed resolution error.</summary>
public sealed record ToolConsentScopeResolution(
    ToolRequestedScope? Scope,
    string? Error)
{
    public bool IsSuccess => Scope is not null && Error is null;

    /// <summary>Creates a successful scope resolution.</summary>
    public static ToolConsentScopeResolution Success(ToolRequestedScope scope) =>
        new(scope ?? throw new ArgumentNullException(nameof(scope)), Error: null);

    /// <summary>Creates a failed scope resolution that must not produce a Grant.</summary>
    public static ToolConsentScopeResolution Failure(string error) =>
        new(Scope: null, error ?? throw new ArgumentNullException(nameof(error)));
}

/// <summary>Resolves a tool invocation into a side-effect-free minimum permission scope.</summary>
public interface IToolConsentScopeResolver
{
    public ToolConsentScopeResolution Resolve(
        ToolInvocation invocation,
        ToolDescriptor descriptor,
        string workspaceRoot);
}
