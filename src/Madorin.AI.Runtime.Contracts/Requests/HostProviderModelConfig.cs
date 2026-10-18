namespace Madorin.AI.Runtime.Contracts;

/// <summary>宿主声明的模型限制；空值表示未知，不能据此假定无限窗口。</summary>
public sealed record HostProviderModelConfig(
    string Id,
    int? ContextWindow = null,
    int? MaxOutputTokens = null);
