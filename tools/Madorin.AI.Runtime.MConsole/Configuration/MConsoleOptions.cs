using System.Text.Json.Serialization;

namespace Madorin.AI.Runtime.MConsole.Configuration;

public sealed class MConsoleOptions
{
    [JsonPropertyName("runtimeExecutablePath")]
    public string RuntimeExecutablePath { get; set; } = string.Empty;

    [JsonPropertyName("defaultWorkspace")]
    public string DefaultWorkspace { get; set; } = string.Empty;

    [JsonPropertyName("defaultDataDir")]
    public string DefaultDataDirectory { get; set; } = string.Empty;

    [JsonPropertyName("defaultLogLevel")]
    public string DefaultLogLevel { get; set; } = "Information";

    [JsonPropertyName("defaultNoColor")]
    public bool DefaultNoColor { get; set; } = true;

    [JsonPropertyName("defaultJson")]
    public bool DefaultJson { get; set; } = true;

    [JsonPropertyName("mconsoleHome")]
    public string MConsoleHome { get; set; } = "~/.madorin-mconsole";

    [JsonPropertyName("startupTimeoutSeconds")]
    public int StartupTimeoutSeconds { get; set; } = 30;

    [JsonPropertyName("commandTimeoutSeconds")]
    public int CommandTimeoutSeconds { get; set; } = 120;

    [JsonPropertyName("callbackTimeoutSeconds")]
    public int CallbackTimeoutSeconds { get; set; } = 30;

    [JsonPropertyName("maxCallbackConcurrency")]
    public int MaxCallbackConcurrency { get; set; } = 4;

    [JsonPropertyName("monitorRefreshSeconds")]
    public int MonitorRefreshSeconds { get; set; } = 2;

    public void Validate()
    {
        if (StartupTimeoutSeconds is < 1 or > 600)
        {
            throw new InvalidDataException("startupTimeoutSeconds must be between 1 and 600.");
        }

        if (CommandTimeoutSeconds is < 1 or > 86_400)
        {
            throw new InvalidDataException("commandTimeoutSeconds must be between 1 and 86400.");
        }

        if (CallbackTimeoutSeconds is < 1 or > 600)
        {
            throw new InvalidDataException("callbackTimeoutSeconds must be between 1 and 600.");
        }

        if (MaxCallbackConcurrency is < 1 or > 128)
        {
            throw new InvalidDataException("maxCallbackConcurrency must be between 1 and 128.");
        }

        if (MonitorRefreshSeconds is < 1 or > 300)
        {
            throw new InvalidDataException("monitorRefreshSeconds must be between 1 and 300.");
        }
    }
}
