namespace Madorin.AI.Multimedia.Core.Protocol;

/// <summary>校验模型 ID 的传输结构，不维护供应商模型白名单。</summary>
public static class ModelIds
{
    /// <summary>允许服务自定义名称，仅拒绝空值和控制字符。</summary>
    public static bool IsValid(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
}
