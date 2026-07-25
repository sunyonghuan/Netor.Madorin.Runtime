using System.Reflection;
using System.Runtime.InteropServices;
using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Cli.Commands;

internal static class RuntimeVersionInfoProvider
{
    public static RuntimeVersionInfo Create()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.1.0";
        return new RuntimeVersionInfo(
            "Madorin.AI.Runtime",
            version,
            ProtocolVersions.Current,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant());
    }
}
