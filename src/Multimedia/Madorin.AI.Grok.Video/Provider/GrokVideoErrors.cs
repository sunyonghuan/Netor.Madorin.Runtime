namespace Madorin.AI.Grok.Video.Provider;

internal static class GrokVideoErrors
{
    public static bool IsSafeProviderValue(string? value, string? apiKey, int maxLength = 512) =>
        value is { Length: > 0 } && value.Length <= maxLength &&
        (string.IsNullOrEmpty(apiKey) || !value.Contains(apiKey, StringComparison.Ordinal)) &&
        !value.Any(char.IsControl);

    public static string? SafeCode(string? code, string? apiKey) =>
        code is not null && IsSafeProviderValue(code, apiKey, 128) &&
        code.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.') ? code : null;

    public static string Normalize(string? code, string fallback = "provider_error") => code switch
    {
        "insufficient_quota" or "billing_hard_limit_reached" => "insufficient_balance",
        "content_policy_violation" => "content_policy_violation",
        _ => fallback
    };
}
