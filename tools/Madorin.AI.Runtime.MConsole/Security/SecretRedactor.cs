namespace Madorin.AI.Runtime.MConsole.Security;

public static class SecretRedactor
{
    private const string Redacted = "<redacted>";

    private static readonly string[] SensitiveFragments =
    [
        "secret",
        "api-key",
        "apikey",
        "credential",
        "password",
        "token",
    ];

    public static string[] RedactArguments(IReadOnlyList<string> arguments)
    {
        var result = arguments.ToArray();
        for (var index = 0; index < result.Length; index++)
        {
            var argument = result[index];
            var separatorIndex = argument.IndexOf('=', StringComparison.Ordinal);
            var optionName = separatorIndex >= 0 ? argument[..separatorIndex] : argument;

            if (!IsSensitiveOption(optionName))
            {
                continue;
            }

            if (separatorIndex >= 0)
            {
                result[index] = $"{optionName}={Redacted}";
            }
            else if (index + 1 < result.Length)
            {
                result[++index] = Redacted;
            }
        }

        return result;
    }

    public static string RedactText(string value, IEnumerable<string?> secrets)
    {
        var redacted = value;
        foreach (var secret in secrets)
        {
            if (!string.IsNullOrEmpty(secret))
            {
                redacted = redacted.Replace(secret, Redacted, StringComparison.Ordinal);
            }
        }

        return redacted;
    }

    private static bool IsSensitiveOption(string optionName) =>
        SensitiveFragments.Any(fragment =>
            optionName.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
