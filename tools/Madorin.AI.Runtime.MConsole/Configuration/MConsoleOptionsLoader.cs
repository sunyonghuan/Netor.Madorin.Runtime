using System.Text.Json;

using Madorin.AI.Runtime.MConsole.Serialization;

namespace Madorin.AI.Runtime.MConsole.Configuration;

public static class MConsoleOptionsLoader
{
    public static async Task<MConsoleOptions> LoadAsync(
        string? configurationPath = null,
        CancellationToken cancellationToken = default)
    {
        var path = ResolveConfigurationPath(configurationPath);
        MConsoleOptions options;

        if (path is null)
        {
            options = new MConsoleOptions();
        }
        else
        {
            await using var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });

            options = await JsonSerializer.DeserializeAsync(
                    stream,
                    MConsoleJsonContext.Default.MConsoleOptions,
                    cancellationToken)
                ?? throw new InvalidDataException($"Configuration file '{path}' is empty.");
        }

        ApplyEnvironmentOverrides(options);
        options.Validate();
        return options;
    }

    private static string? ResolveConfigurationPath(string? configurationPath)
    {
        if (!string.IsNullOrWhiteSpace(configurationPath))
        {
            var explicitPath = Path.GetFullPath(configurationPath);
            return File.Exists(explicitPath)
                ? explicitPath
                : throw new FileNotFoundException("MConsole configuration file was not found.", explicitPath);
        }

        string[] candidates =
        [
            Path.Combine(Environment.CurrentDirectory, "appsettings.mconsole.json"),
            Path.Combine(AppContext.BaseDirectory, "appsettings.mconsole.json"),
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    private static void ApplyEnvironmentOverrides(MConsoleOptions options)
    {
        options.RuntimeExecutablePath = Get("MCONSOLE_RUNTIME_EXECUTABLE") ?? options.RuntimeExecutablePath;
        options.DefaultWorkspace = Get("MCONSOLE_DEFAULT_WORKSPACE") ?? options.DefaultWorkspace;
        options.DefaultDataDirectory = Get("MCONSOLE_DEFAULT_DATA_DIR") ?? options.DefaultDataDirectory;
        options.MConsoleHome = Get("MCONSOLE_HOME") ?? options.MConsoleHome;

        if (int.TryParse(Get("MCONSOLE_STARTUP_TIMEOUT_SECONDS"), out var startupTimeout))
        {
            options.StartupTimeoutSeconds = startupTimeout;
        }

        if (int.TryParse(Get("MCONSOLE_COMMAND_TIMEOUT_SECONDS"), out var commandTimeout))
        {
            options.CommandTimeoutSeconds = commandTimeout;
        }
    }

    private static string? Get(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
