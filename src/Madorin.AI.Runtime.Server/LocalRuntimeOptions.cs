using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Services.Memory;
using Madorin.AI.Runtime.Services.Tools;

namespace Madorin.AI.Runtime.Server;

public sealed record LocalRuntimeOptions(
    string WorkspaceRoot,
    string DataDirectory,
    string LogDirectory,
    string RuntimeInstanceId,
    Func<NextTurnSelection, IRuntimeProviderAdapter> ProviderResolver)
{
    /// <summary>Prebuilt memory service shared by standalone Runtime composition and REPL commands.</summary>
    public MemoryFileService? MemoryFiles { get; init; }

    public string? MemoryUserHome { get; init; }

    /// <summary>Local interactive authority used by standalone Runtime executions.</summary>
    public IToolConsentHandler? ToolConsentHandler { get; init; }

    public RuntimeLimits RuntimeLimits { get; init; } = new(
        MaxToolResultBytesPerInvocation: 4 * 1024 * 1024);

    public long MaxGlobalBlobBytes { get; init; } = 8L * 1024 * 1024 * 1024;

    public TimeSpan WorkspaceLockTimeout { get; init; } = TimeSpan.Zero;

    public RuntimeDiagnosticLogLevel MinimumLogLevel { get; init; } =
        RuntimeDiagnosticLogLevel.Information;

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Additional host Skill directories, appended after automatic sources.</summary>
    public IReadOnlyList<SkillSourceDescriptor>? SkillSources { get; init; }
}
