using System.CommandLine;
using System.Text.Json;
using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Cli.Commands;

internal static class DiagnosticCommands
{
    public static Command CreateVersion(Option<bool> jsonOption, TextWriter output)
    {
        var command = new Command("version", "Display program and protocol versions.");
        command.SetAction(parseResult =>
        {
            var info = RuntimeVersionInfoProvider.Create();
            if (parseResult.GetValue(jsonOption))
            {
                CliOutput.WriteJson(output, writer =>
                    JsonSerializer.Serialize(
                        writer,
                        info,
                        RuntimeJsonContext.Default.RuntimeVersionInfo));
            }
            else
            {
                output.WriteLine(
                    $"madorin {info.Version} (protocol {info.ProtocolVersion})");
                output.WriteLine($"Runtime: {info.Product}");
                output.WriteLine(
                    $"Platform: {info.Framework} / {info.Platform} {info.Architecture}");
            }

            return ExitCodes.Success;
        });

        return command;
    }

    public static Command CreateDoctor(
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        Option<string?> dataDirectoryOption,
        TextReader input,
        TextWriter output,
        string? configDirectory = null,
        string? memoryUserHome = null,
        Func<StandaloneConfig, string, IRuntimeProviderAdapter>? providerAdapterFactory = null) =>
        DoctorCommand.Create(
            jsonOption,
            workspaceOption,
            dataDirectoryOption,
            input,
            output,
            configDirectory,
            memoryUserHome,
            providerAdapterFactory);
}
