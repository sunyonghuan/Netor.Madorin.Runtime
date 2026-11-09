using System.Collections.Frozen;

namespace Madorin.AI.Runtime.Services.Skills;

/// <summary>Defines the non-secret process environment inherited by Skill interpreters.</summary>
public static class SkillScriptEnvironmentPolicy
{
    public static IReadOnlySet<string> InheritedVariableNames { get; } = CreateVariableNames();

    public static IReadOnlyDictionary<string, string> FixedVariableValues { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PYTHONIOENCODING"] = "utf-8",
            ["PYTHONUTF8"] = "1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1"
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static FrozenSet<string> CreateVariableNames()
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var names = OperatingSystem.IsWindows()
            ? new[]
            {
                "PATH", "SystemRoot", "windir", "TEMP", "TMP", "HOME", "HOMEDRIVE", "HOMEPATH",
                "USERPROFILE", "LOCALAPPDATA", "APPDATA", "ProgramFiles", "ProgramFiles(x86)",
                "ProgramData", "COMSPEC", "OS", "PATHEXT", "PSModulePath", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_CLI_HOME",
                "NUGET_PACKAGES", "PYTHONIOENCODING", "PYTHONUTF8", "DOTNET_CLI_TELEMETRY_OPTOUT",
                "DOTNET_NOLOGO"
            }
            : new[]
            {
                "PATH", "HOME", "TMPDIR", "TEMP", "TMP", "DOTNET_ROOT", "DOTNET_CLI_HOME",
                "NUGET_PACKAGES", "PYTHONIOENCODING", "PYTHONUTF8", "DOTNET_CLI_TELEMETRY_OPTOUT",
                "DOTNET_NOLOGO"
            };
        return names.ToFrozenSet(comparer);
    }
}
