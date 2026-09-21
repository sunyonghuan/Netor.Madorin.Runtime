namespace Madorin.AI.Runtime.Contracts;

/// <summary>
/// 用于计算 ContextPolicy 哈希的规范化 DTO。
/// 此类型必须在 RuntimeJsonContext 中注册以支持 AOT。
/// </summary>
public sealed record ContextPolicyHashDto
{
    public required string Strategy { get; init; }
    public int? MaxTokens { get; init; }
    public int? TailMessageCount { get; init; }
    public bool? PreserveToolResults { get; init; }
    public bool? PreserveReasoning { get; init; }
    public bool? SummaryRequired { get; init; }
}
