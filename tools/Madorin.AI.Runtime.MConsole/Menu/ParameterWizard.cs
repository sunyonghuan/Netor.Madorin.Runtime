using System.Globalization;

namespace Madorin.AI.Runtime.MConsole.Menu;

public sealed class ParameterWizard(TextReader input, TextWriter output)
{
    public async Task<CommandInvocation> CollectAsync(
        CommandDefinition definition,
        CommandDefaults defaults,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var parameter in definition.Parameters)
        {
            while (true)
            {
                await WritePromptAsync(parameter);
                var value = await input.ReadLineAsync(cancellationToken);
                value = string.IsNullOrWhiteSpace(value) ? parameter.DefaultValue : value.Trim();
                if (string.IsNullOrWhiteSpace(value) && !parameter.Required)
                {
                    break;
                }

                if (TryValidate(parameter, value, out var error))
                {
                    values[parameter.Name] = Normalize(parameter, value!);
                    break;
                }

                await output.WriteLineAsync(error);
            }
        }

        return Build(definition, defaults, values);
    }

    public static CommandInvocation Build(
        CommandDefinition definition,
        CommandDefaults defaults,
        IReadOnlyDictionary<string, string?> values,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(values);
        var arguments = new List<string>(definition.CommandPath);
        string? standardInput = null;
        foreach (var parameter in definition.Parameters)
        {
            if (!values.TryGetValue(parameter.Name, out var value)
                || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (!TryValidate(parameter, value, out var error))
            {
                throw new ArgumentException(error, nameof(values));
            }

            value = Normalize(parameter, value);
            if (parameter.Kind is ParameterKind.StdinLine)
            {
                standardInput = value + Environment.NewLine;
            }
            else if (parameter.Kind is ParameterKind.Boolean)
            {
                if (bool.Parse(value))
                {
                    arguments.Add(parameter.Name);
                }
            }
            else if (parameter.Positional)
            {
                arguments.Add(value);
            }
            else
            {
                arguments.Add(parameter.Name);
                arguments.Add(value);
            }
        }

        AddGlobalArguments(arguments, definition, defaults, values);
        return new CommandInvocation(
            definition,
            arguments,
            new Dictionary<string, string?>(values, StringComparer.Ordinal),
            standardInput,
            environment is null
                ? new Dictionary<string, string?>(StringComparer.Ordinal)
                : new Dictionary<string, string?>(environment, StringComparer.Ordinal));
    }

    private static void AddGlobalArguments(
        List<string> arguments,
        CommandDefinition definition,
        CommandDefaults defaults,
        IReadOnlyDictionary<string, string?> values)
    {
        if (!string.IsNullOrWhiteSpace(defaults.Workspace))
        {
            arguments.Add("--workspace");
            arguments.Add(defaults.Workspace);
        }

        if (!string.IsNullOrWhiteSpace(defaults.DataDirectory))
        {
            arguments.Add("--data-dir");
            arguments.Add(defaults.DataDirectory);
        }

        if (!string.IsNullOrWhiteSpace(defaults.LogLevel))
        {
            arguments.Add("--log-level");
            arguments.Add(defaults.LogLevel);
        }

        if (defaults.NoColor)
        {
            arguments.Add("--no-color");
        }

        var isInteractiveRun = definition.Id == "run"
            && !HasValue(values, "--input")
            && !HasValue(values, "--input-file");
        if (defaults.Json && !isInteractiveRun)
        {
            arguments.Add("--json");
        }
    }

    private async Task WritePromptAsync(ParameterDefinition parameter)
    {
        var allowed = parameter.AllowedValues is { Length: > 0 }
            ? $" [{string.Join('/', parameter.AllowedValues)}]"
            : string.Empty;
        var defaultText = parameter.DefaultValue is not null
            ? $" (default: {parameter.DefaultValue})"
            : string.Empty;
        await output.WriteAsync($"{parameter.Prompt}{allowed}{defaultText}: ");
    }

    private static bool TryValidate(
        ParameterDefinition parameter,
        string? value,
        out string error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            error = parameter.Required
                ? $"{parameter.Name} is required."
                : string.Empty;
            return !parameter.Required;
        }

        if (parameter.AllowedValues is { Length: > 0 }
            && !parameter.AllowedValues.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            error = $"{parameter.Name} must be one of: {string.Join(", ", parameter.AllowedValues)}.";
            return false;
        }

        if (parameter.Kind is ParameterKind.Boolean && !TryParseBoolean(value, out _))
        {
            error = $"{parameter.Name} must be yes/no or true/false.";
            return false;
        }

        if (parameter.Kind is ParameterKind.WholeNumber)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                || parameter.Minimum is { } minimum && number < minimum
                || parameter.Maximum is { } maximum && number > maximum)
            {
                error = $"{parameter.Name} must be an integer between " +
                    $"{parameter.Minimum?.ToString(CultureInfo.InvariantCulture) ?? "-infinity"} and " +
                    $"{parameter.Maximum?.ToString(CultureInfo.InvariantCulture) ?? "+infinity"}.";
                return false;
            }
        }

        if (parameter.Kind is ParameterKind.DateTimeIso8601
            && (!DateTimeOffset.TryParseExact(
                    value,
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out _)
                && !DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out _)))
        {
            error = $"{parameter.Name} must be an ISO-8601 timestamp with an offset.";
            return false;
        }

        if (parameter.Kind is ParameterKind.Path && parameter.MustExist
            && !File.Exists(value) && !Directory.Exists(value))
        {
            error = $"Path '{value}' does not exist.";
            return false;
        }

        if (parameter.Kind is ParameterKind.SecretEnvName
            && (value.Contains('=', StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(value))))
        {
            error = $"Secret environment variable '{value}' is invalid, missing, or empty.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static string Normalize(ParameterDefinition parameter, string value)
    {
        if (parameter.Kind is ParameterKind.Boolean)
        {
            _ = TryParseBoolean(value, out var parsed);
            return parsed.ToString(CultureInfo.InvariantCulture);
        }

        return parameter.Kind is ParameterKind.Path
            ? Path.GetFullPath(value)
            : value;
    }

    private static bool TryParseBoolean(string value, out bool parsed)
    {
        if (bool.TryParse(value, out parsed))
        {
            return true;
        }

        if (value is "y" or "yes" or "1")
        {
            parsed = true;
            return true;
        }

        if (value is "n" or "no" or "0")
        {
            parsed = false;
            return true;
        }

        return false;
    }

    private static bool HasValue(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value);
}
