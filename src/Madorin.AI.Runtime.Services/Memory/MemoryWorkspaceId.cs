using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Madorin.AI.Runtime.Services.Memory;

/// <summary>
/// 记忆工作区标识计算。宿主项目 Id 是唯一输入，路径不参与计算。
/// </summary>
public static class MemoryWorkspaceId
{
    /// <summary>
    /// 将项目 Id 转换为记忆库使用的 32 位小写 MD5 标识。
    /// </summary>
    [SuppressMessage(
        "Security",
        "CA5351",
        Justification = "记忆工作区标识只需要稳定且与历史数据一致的键，不承担密码学安全职责。")]
    public static string Compute(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var normalized = projectId.Trim();
        return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}
