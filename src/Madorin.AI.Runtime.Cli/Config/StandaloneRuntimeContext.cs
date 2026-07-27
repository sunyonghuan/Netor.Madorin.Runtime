using System.Security.Cryptography;
using System.Text;

namespace Madorin.AI.Runtime.Cli.Config;

/// <summary>
/// Immutable snapshot of the resolved paths and identifiers for a standalone runtime instance.
/// </summary>
public sealed record StandaloneRuntimeContext(
    string WorkspaceRoot,
    string WorkspaceKey,
    string RuntimeInstanceId,
    string ConfigDirectory,
    string DataDirectory,
    string LogDirectory,
    bool UsesDefaultDataDirectory,
    string? LegacyDataDirectory)
{
    /// <summary>
    /// Creates a new <see cref="StandaloneRuntimeContext"/> with normalized absolute paths.
    /// </summary>
    /// <param name="workspace">Workspace root; defaults to the current directory.</param>
    /// <param name="dataDirectory">Explicit data directory override; defaults to <c>&lt;workspace&gt;/.madorin</c>.</param>
    /// <param name="configDirectory">Config directory; defaults to <see cref="StandaloneConfigLoader.GetDefaultConfigDirectory"/>.</param>
    public static StandaloneRuntimeContext Create(
        string? workspace = null,
        string? dataDirectory = null,
        string? configDirectory = null)
    {
        if (workspace is not null && string.IsNullOrWhiteSpace(workspace))
        {
            throw new ArgumentException("Workspace path must not be whitespace-only.", nameof(workspace));
        }

        if (dataDirectory is not null && string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException("Data directory path must not be whitespace-only.", nameof(dataDirectory));
        }

        if (configDirectory is not null && string.IsNullOrWhiteSpace(configDirectory))
        {
            throw new ArgumentException("Config directory path must not be whitespace-only.", nameof(configDirectory));
        }

        var normalizedWorkspace = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(workspace ?? Directory.GetCurrentDirectory()));

        var normalizedConfig = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(configDirectory ?? StandaloneConfigLoader.GetDefaultConfigDirectory()));

        var workspaceKeyInput = OperatingSystem.IsWindows()
            ? normalizedWorkspace.ToUpperInvariant()
            : normalizedWorkspace;

        var workspaceKeyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(workspaceKeyInput));
        var workspaceKey = Convert.ToHexStringLower(workspaceKeyBytes);

        var runtimeInstanceId = Guid.NewGuid().ToString("N");

        var usesDefaultDataDirectory = dataDirectory is null;
        var normalizedData = dataDirectory is not null
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory))
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(
                Path.Combine(normalizedWorkspace, ".madorin")));

        var legacyDataDirectory = usesDefaultDataDirectory
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(
                Path.Combine(normalizedConfig, "data", "workspaces", workspaceKey)))
            : null;

        var logDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            Path.Combine(normalizedData, "logs", runtimeInstanceId)));

        return new StandaloneRuntimeContext(
            normalizedWorkspace,
            workspaceKey,
            runtimeInstanceId,
            normalizedConfig,
            normalizedData,
            logDirectory,
            usesDefaultDataDirectory,
            legacyDataDirectory);
    }

    /// <summary>
    /// Creates a context and prepares its data directory, including safe migration from the
    /// legacy user-level workspace directory when the default data location is used.
    /// </summary>
    public static async Task<StandaloneRuntimeContext> CreatePreparedAsync(
        string? workspace = null,
        string? dataDirectory = null,
        string? configDirectory = null,
        CancellationToken ct = default)
    {
        var context = Create(workspace, dataDirectory, configDirectory);
        await StandaloneWorkspaceDataMigrator.PrepareAsync(context, ct).ConfigureAwait(false);
        return context;
    }
}
