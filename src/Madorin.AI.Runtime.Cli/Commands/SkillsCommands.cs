using System.CommandLine;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Services.Skills;
using Madorin.AI.Runtime.Tools.Builtin;

namespace Madorin.AI.Runtime.Cli.Commands;

internal static class SkillsCommands
{
    public static Command Create(
        Option<bool> jsonOption,
        Option<string?> workspaceOption,
        Option<string?> dataDirectoryOption,
        TextWriter output)
    {
        var command = new Command("skill", "Inspect and refresh local Skills.");
        command.Subcommands.Add(CreateList(jsonOption, workspaceOption, dataDirectoryOption, output));
        command.Subcommands.Add(CreateShow(jsonOption, workspaceOption, dataDirectoryOption, output));
        command.Subcommands.Add(CreateValidate(jsonOption, workspaceOption, dataDirectoryOption, output));
        command.Subcommands.Add(CreateRefresh(jsonOption, workspaceOption, dataDirectoryOption, output));
        return command;
    }

    private static Command CreateList(Option<bool> json, Option<string?> workspace, Option<string?> data, TextWriter output)
    {
        var command = new Command("list", "List effective Skills.");
        var scope = CreateScopeOption();
        command.Options.Add(scope);
        command.SetAction(async parse =>
        {
            if (!TryGetScope(parse.GetValue(scope), out var selectedScope))
            {
                return ExitCodes.InvalidArguments;
            }

            var summary = await LoadAsync(parse, workspace, data, selectedScope).ConfigureAwait(false);
            if (CliOutput.IsJson(parse, json))
            {
                WriteJson(output, summary);
            }
            else
            {
                foreach (var skill in summary.Skills)
                {
                    output.WriteLine($"{skill.Name}\t{skill.SourceId}\teffective\t{skill.Description}");
                }
            }

            return ExitCodes.Success;
        });
        return command;
    }

    private static Command CreateShow(Option<bool> json, Option<string?> workspace, Option<string?> data, TextWriter output)
    {
        var argument = new Argument<string>("name");
        var command = new Command("show", "Show one Skill definition.");
        var scope = CreateScopeOption();
        command.Arguments.Add(argument);
        command.Options.Add(scope);
        command.SetAction(async parse =>
        {
            var name = parse.GetValue(argument);
            if (string.IsNullOrWhiteSpace(name))
            {
                return ExitCodes.InvalidArguments;
            }

            if (!TryGetScope(parse.GetValue(scope), out var selectedScope))
            {
                return ExitCodes.InvalidArguments;
            }

            await using var catalog = await CreateCatalogAsync(parse, workspace, data, selectedScope).ConfigureAwait(false);
            try
            {
                var content = await catalog.LoadSkillAsync(name).ConfigureAwait(false);
                _ = catalog.TryGetSkill(name, out var skill);
                var source = catalog.Current.Sources.FirstOrDefault(
                    item => string.Equals(item.SourceId, skill!.SourceId, StringComparison.Ordinal));
                SplitDefinition(content, out var frontmatter, out var body);
                if (CliOutput.IsJson(parse, json))
                {
                    CliOutput.WriteJson(output, writer =>
                    {
                        writer.WriteStartObject();
                        writer.WriteString("command", "skill show");
                        writer.WriteString("catalogVersion", catalog.Current.Version);
                        writer.WriteString("name", skill!.Name);
                        writer.WriteString("description", skill.Description);
                        writer.WriteString("sourceId", skill.SourceId);
                        if (source is not null)
                        {
                            writer.WriteString("sourceKind", source.Kind.ToString());
                            writer.WriteString("sourceLocation", source.Location);
                        }

                        writer.WriteString("contentHash", skill.ContentHash);
                        writer.WriteString("frontmatter", frontmatter);
                        writer.WriteString("body", body);
                        WriteStringArray(writer, "resources", skill.Resources);
                        WriteStringArray(writer, "scripts", skill.Scripts);
                        writer.WriteEndObject();
                    });
                }
                else
                {
                    output.WriteLine($"Name: {skill!.Name}");
                    output.WriteLine($"Description: {skill.Description}");
                    output.WriteLine($"Source: {skill.SourceId} ({source?.Kind.ToString() ?? "unknown"})");
                    if (source is not null)
                    {
                        output.WriteLine($"Location: {source.Location}");
                    }

                    output.WriteLine($"Catalog: {catalog.Current.Version}");
                    output.WriteLine($"Content hash: {skill.ContentHash}");
                    output.WriteLine("Frontmatter:");
                    output.WriteLine("---");
                    output.WriteLine(frontmatter);
                    output.WriteLine("---");
                    WriteList(output, "Resources", skill.Resources);
                    WriteList(output, "Scripts", skill.Scripts);
                    output.WriteLine("Body:");
                    output.Write(body);
                }

                return ExitCodes.Success;
            }
            catch (KeyNotFoundException)
            {
                return ExitCodes.InvalidArguments;
            }
        });
        return command;
    }

    private static Command CreateValidate(Option<bool> json, Option<string?> workspace, Option<string?> data, TextWriter output)
    {
        var command = new Command("validate", "Validate Skills without executing scripts.");
        var scope = CreateScopeOption();
        command.Options.Add(scope);
        command.SetAction(async parse =>
        {
            if (!TryGetScope(parse.GetValue(scope), out var selectedScope))
            {
                return ExitCodes.InvalidArguments;
            }

            await using var catalog = await CreateCatalogAsync(parse, workspace, data, selectedScope).ConfigureAwait(false);
            var summary = await ValidateAsync(catalog).ConfigureAwait(false);
            if (CliOutput.IsJson(parse, json))
            {
                WriteJson(output, summary);
            }
            else
            {
                foreach (var diagnostic in summary.Diagnostics)
                {
                    output.WriteLine($"{diagnostic.Severity}\t{diagnostic.Code}\t{diagnostic.Message}");
                }

                output.WriteLine($"{summary.SkillCount} Skill(s) validated.");
            }

            return summary.Diagnostics.Any(static x => x.Severity.Equals("error", StringComparison.OrdinalIgnoreCase))
                ? ExitCodes.GeneralError
                : ExitCodes.Success;
        });
        return command;
    }

    private static Command CreateRefresh(Option<bool> json, Option<string?> workspace, Option<string?> data, TextWriter output)
    {
        var command = new Command("refresh", "Refresh the local Skill catalog.");
        command.SetAction(async parse =>
        {
            var summary = await LoadAsync(parse, workspace, data).ConfigureAwait(false);
            if (CliOutput.IsJson(parse, json))
            {
                WriteJson(output, summary);
            }
            else
            {
                output.WriteLine($"Catalog {summary.CatalogVersion}: {summary.SkillCount} Skill(s).");
            }

            return ExitCodes.Success;
        });
        return command;
    }

    private static async Task<SkillCatalogSummary> LoadAsync(ParseResult parse, Option<string?> workspace, Option<string?> data)
        => await LoadAsync(parse, workspace, data, SkillCatalogSourceScope.Effective).ConfigureAwait(false);

    private static async Task<SkillCatalogSummary> LoadAsync(
        ParseResult parse,
        Option<string?> workspace,
        Option<string?> data,
        SkillCatalogSourceScope scope)
    {
        await using var catalog = await CreateCatalogAsync(parse, workspace, data, scope).ConfigureAwait(false);
        return catalog.GetSummary();
    }

    private static async Task<SkillCatalogService> CreateCatalogAsync(
        ParseResult parse,
        Option<string?> workspace,
        Option<string?> data,
        SkillCatalogSourceScope scope = SkillCatalogSourceScope.Effective)
    {
        var workspaceRoot = Path.GetFullPath(parse.GetValue(workspace) ?? Directory.GetCurrentDirectory());
        var dataDirectory = Path.GetFullPath(parse.GetValue(data) ?? Path.Combine(workspaceRoot, ".madorin"));
        var catalog = new SkillCatalogService(
            workspaceRoot,
            dataDirectory,
            $"cli-{Guid.NewGuid():N}",
            sourceScope: scope);
        await catalog.InitializeAsync().ConfigureAwait(false);
        return catalog;
    }

    private static Option<string?> CreateScopeOption() => CommandOptions.Create<string?>(
        "--scope",
        "Select effective, workspace, or global Skills.");

    private static bool TryGetScope(string? value, out SkillCatalogSourceScope scope)
    {
        switch (value?.Trim().ToLowerInvariant() ?? "effective")
        {
            case "effective":
                scope = SkillCatalogSourceScope.Effective;
                return true;
            case "workspace":
                scope = SkillCatalogSourceScope.Workspace;
                return true;
            case "global":
                scope = SkillCatalogSourceScope.UserGlobal;
                return true;
            default:
                scope = default;
                return false;
        }
    }

    private static async Task<SkillCatalogSummary> ValidateAsync(SkillCatalogService catalog)
    {
        var summary = catalog.GetSummary();
        var diagnostics = summary.Diagnostics.ToList();
        var interpreterResults = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var availableTools = new BuiltinToolRegistry()
            .GetTools()
            .Select(static tool => tool.ToolId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var skill in summary.Skills)
        {
            foreach (var script in skill.Scripts)
            {
                var extension = Path.GetExtension(script);
                if (!interpreterResults.TryGetValue(extension, out var interpreter))
                {
                    try
                    {
                        interpreter = await AgentFileSkillScriptRunner.ResolveInterpreterNameAsync(extension)
                            .ConfigureAwait(false);
                    }
                    catch (SkillScriptException ex)
                        when (ex.Code == RuntimeErrorCodes.SkillScriptInterpreterUnavailable)
                    {
                        interpreter = null;
                    }

                    interpreterResults[extension] = interpreter;
                }

                if (interpreter is null)
                {
                    diagnostics.Add(new SkillCatalogDiagnostic(
                        RuntimeErrorCodes.SkillScriptInterpreterUnavailable,
                        "warning",
                        $"No fixed interpreter is available for script extension '{extension}'.",
                        skill.SourceId,
                        skill.Name,
                        script));
                }
            }

            var definition = await catalog.LoadSkillAsync(skill.Name).ConfigureAwait(false);
            SplitDefinition(definition, out var frontmatter, out _);
            foreach (var toolId in ParseAllowedTools(frontmatter))
            {
                if (!availableTools.Contains(toolId))
                {
                    diagnostics.Add(new SkillCatalogDiagnostic(
                        RuntimeErrorCodes.SkillToolUnavailable,
                        "warning",
                        $"Declared tool dependency '{toolId}' is not available in the standalone Tool Catalog.",
                        skill.SourceId,
                        skill.Name));
                }
            }
        }

        return summary with { Diagnostics = diagnostics.ToArray() };
    }

    private static void SplitDefinition(
        string content,
        out string frontmatter,
        out string body)
    {
        using var reader = new StringReader(content.TrimStart('\uFEFF'));
        if (!string.Equals(reader.ReadLine()?.Trim(), "---", StringComparison.Ordinal))
        {
            frontmatter = string.Empty;
            body = content;
            return;
        }

        var lines = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) is not null
            && !string.Equals(line.Trim(), "---", StringComparison.Ordinal))
        {
            lines.Add(line);
        }

        frontmatter = string.Join(Environment.NewLine, lines);
        body = reader.ReadToEnd().TrimStart('\r', '\n');
    }

    private static string[] ParseAllowedTools(string frontmatter)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var lines = frontmatter.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("allowed-tools:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AddToolTokens(result, trimmed["allowed-tools:".Length..]);
            while (index + 1 < lines.Length)
            {
                var next = lines[index + 1];
                var nextTrimmed = next.Trim();
                if (!nextTrimmed.StartsWith('-')
                    || next.Length == nextTrimmed.Length)
                {
                    break;
                }

                index++;
                AddToolTokens(result, nextTrimmed[1..]);
            }
        }

        return result.Order(StringComparer.Ordinal).ToArray();
    }

    private static void AddToolTokens(HashSet<string> result, string value)
    {
        var normalized = value.Trim().Trim('[', ']', '"', '\'');
        foreach (var token in normalized.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            result.Add(token.Trim('"', '\''));
        }
    }

    private static void WriteStringArray(Utf8JsonWriter writer, string propertyName, IEnumerable<string> values)
    {
        writer.WriteStartArray(propertyName);
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static void WriteList(TextWriter output, string title, IReadOnlyCollection<string> values)
    {
        output.WriteLine($"{title} ({values.Count}):");
        foreach (var value in values)
        {
            output.WriteLine($"  {value}");
        }
    }

    private static void WriteJson(TextWriter output, SkillCatalogSummary summary) =>
        CliOutput.WriteJson(output, writer => JsonSerializer.Serialize(writer, summary, RuntimeJsonContext.Default.SkillCatalogSummary));
}
