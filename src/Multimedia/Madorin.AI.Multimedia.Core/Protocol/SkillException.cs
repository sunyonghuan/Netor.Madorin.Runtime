namespace Madorin.AI.Multimedia.Core.Protocol;

public sealed class SkillException(string code, string message, string? providerCode = null) : Exception(message)
{
    public string Code { get; } = code;
    public string? ProviderCode { get; } = providerCode;
}
