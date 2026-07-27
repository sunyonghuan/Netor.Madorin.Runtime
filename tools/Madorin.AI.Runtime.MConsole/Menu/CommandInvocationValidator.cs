namespace Madorin.AI.Runtime.MConsole.Menu;

public static class CommandInvocationValidator
{
    public static CommandValidationResult Validate(
        CommandInvocation invocation,
        CommandDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(defaults);
        var errors = new List<string>();
        foreach (var parameter in invocation.Definition.Parameters)
        {
            if (parameter.Required && !HasValue(invocation, parameter.Name))
            {
                errors.Add($"{parameter.Name} is required.");
            }

            if (parameter.MutuallyExclusiveWith is null || !HasValue(invocation, parameter.Name))
            {
                continue;
            }

            errors.AddRange(parameter.MutuallyExclusiveWith
                .Where(name => HasValue(invocation, name))
                .Select(name => $"{parameter.Name} conflicts with {name}."));
        }

        switch (invocation.Definition.Id)
        {
            case "run":
                ValidateRun(invocation, defaults, errors);
                break;
            case "serve" when string.IsNullOrWhiteSpace(defaults.Workspace):
                errors.Add("serve requires --workspace.");
                break;
            case "doctor" when IsEnabled(invocation, "--yes") && !IsEnabled(invocation, "--fix"):
                errors.Add("doctor --yes requires --fix.");
                break;
            case "storage.gc" when IsEnabled(invocation, "--dry-run")
                && IsEnabled(invocation, "--confirm"):
                errors.Add("storage gc --dry-run conflicts with --confirm.");
                break;
            case "session.delete" when !IsEnabled(invocation, "--confirm"):
                errors.Add("session delete requires --confirm.");
                break;
        }

        return errors.Count == 0
            ? CommandValidationResult.Success
            : new CommandValidationResult(false, errors);
    }

    private static void ValidateRun(
        CommandInvocation invocation,
        CommandDefaults defaults,
        List<string> errors)
    {
        var hasInput = HasValue(invocation, "--input");
        var hasInputFile = HasValue(invocation, "--input-file");
        if (hasInput && hasInputFile)
        {
            errors.Add("run --input conflicts with --input-file.");
        }

        if (HasValue(invocation, "--output-file") && !hasInput && !hasInputFile)
        {
            errors.Add("run --output-file requires --input or --input-file.");
        }

        var interactive = !hasInput && !hasInputFile;
        if (interactive && (HasValue(invocation, "--output-format")
            || IsEnabled(invocation, "--no-stream")))
        {
            errors.Add("Interactive run cannot use output format or --no-stream.");
        }

        if (defaults.Json
            && HasValue(invocation, "--output-format")
            && !string.Equals(GetValue(invocation, "--output-format"), "json", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(GetValue(invocation, "--output-format"), "jsonl", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("run --json conflicts with a non-JSON --output-format.");
        }
    }

    private static bool HasValue(CommandInvocation invocation, string name) =>
        invocation.Values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value);

    private static string? GetValue(CommandInvocation invocation, string name) =>
        invocation.Values.TryGetValue(name, out var value) ? value : null;

    private static bool IsEnabled(CommandInvocation invocation, string name) =>
        bool.TryParse(GetValue(invocation, name), out var enabled) && enabled;
}
