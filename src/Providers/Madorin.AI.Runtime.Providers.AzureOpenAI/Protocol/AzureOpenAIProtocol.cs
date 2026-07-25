namespace Madorin.AI.Runtime.Providers.AzureOpenAI;

public static class AzureOpenAIProtocol
{
    public const int MaximumApiVersionLength = 128;

    public const string DefaultApiVersion = "2024-10-21";

    public static bool IsValidApiVersion(string? apiVersion) =>
        !string.IsNullOrWhiteSpace(apiVersion)
        && apiVersion.Length <= MaximumApiVersionLength
        && apiVersion.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-');
}
