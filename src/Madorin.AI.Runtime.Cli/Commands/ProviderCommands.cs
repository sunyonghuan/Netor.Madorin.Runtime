using System.CommandLine;
using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Cli.Commands;

internal static class ProviderCommands
{
    public static Command Create(Option<bool> jsonOption, TextWriter output)
    {
        var command = new Command("provider", "Inspect supported Provider protocols.");
        command.Subcommands.Add(CreateProtocols(jsonOption, output));
        return command;
    }

    private static Command CreateProtocols(Option<bool> jsonOption, TextWriter output)
    {
        var command = new Command("protocols", "List supported Provider protocol names.");
        command.SetAction(parseResult =>
        {
            var protocols = StandaloneProviderProtocolCatalog.All
                .Select(static definition => definition.Protocol)
                .ToArray();
            if (CliOutput.IsJson(parseResult, jsonOption))
            {
                CliOutput.WriteJson(output, writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("command", "provider protocols");
                    writer.WriteStartArray("protocols");
                    foreach (var protocol in protocols)
                    {
                        writer.WriteStringValue(protocol);
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                });
            }
            else
            {
                output.WriteLine("Supported Provider protocols:");
                foreach (var protocol in protocols)
                {
                    output.WriteLine($"- {protocol}");
                }
            }

            return ExitCodes.Success;
        });

        return command;
    }
}
