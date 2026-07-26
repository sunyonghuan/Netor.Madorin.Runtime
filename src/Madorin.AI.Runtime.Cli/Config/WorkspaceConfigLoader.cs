using System.Text.Json;

namespace Madorin.AI.Runtime.Cli.Config;

internal sealed class WorkspaceConfigLoader
{
    internal const int CurrentSchemaVersion = 1;
    internal const long MaximumConfigBytes = 64 * 1024;

    public WorkspaceConfigLoader(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        WorkspaceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        ConfigPath = Path.GetFullPath(Path.Combine(WorkspaceRoot, ".madorin", "config.json"));

        var expectedPrefix = Path.EndsInDirectorySeparator(WorkspaceRoot)
            ? WorkspaceRoot
            : WorkspaceRoot + Path.DirectorySeparatorChar;
        if (!ConfigPath.StartsWith(expectedPrefix, PathComparison))
        {
            throw new ArgumentException(
                "The workspace configuration path must remain under the workspace root.",
                nameof(workspaceRoot));
        }
    }

    public string WorkspaceRoot { get; }

    public string ConfigPath { get; }

    public bool Exists => File.Exists(ConfigPath);

    public async Task<WorkspaceConfig?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(ConfigPath))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(ConfigPath, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
            if (stream.Length > MaximumConfigBytes)
            {
                throw new StandaloneConfigurationException(
                    "WorkspaceConfigurationInvalid",
                    $"Workspace configuration '{ConfigPath}' exceeds the {MaximumConfigBytes}-byte limit.");
            }

            var config = await JsonSerializer.DeserializeAsync(
                    stream,
                    WorkspaceConfigJsonContext.Default.WorkspaceConfig,
                    ct)
                .ConfigureAwait(false);
            Validate(config);
            return config;
        }
        catch (StandaloneConfigurationException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new StandaloneConfigurationException(
                "WorkspaceConfigurationInvalid",
                $"Workspace configuration '{ConfigPath}' contains invalid JSON or unsupported fields.",
                ex);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            throw new StandaloneConfigurationException(
                "WorkspaceConfigurationInvalid",
                $"Workspace configuration '{ConfigPath}' could not be read: {ex.Message}",
                ex);
        }
    }

    private void Validate(WorkspaceConfig? config)
    {
        if (config is null)
        {
            return;
        }

        var schemaVersion = config.SchemaVersion ?? CurrentSchemaVersion;
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new StandaloneConfigurationException(
                "WorkspaceConfigurationInvalid",
                $"Workspace configuration '{ConfigPath}': schemaVersion '{schemaVersion}' is not supported.");
        }

        ValidateReference("selection.provider", config.Selection?.Provider);
        ValidateReference("selection.model", config.Selection?.Model);
        ValidateReference("selection.agent", config.Selection?.Agent);
    }

    private void ValidateReference(string field, string? value)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value))
        {
            throw new StandaloneConfigurationException(
                "WorkspaceConfigurationInvalid",
                $"Workspace configuration '{ConfigPath}': {field} must not be blank.");
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
