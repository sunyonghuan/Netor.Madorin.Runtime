namespace Madorin.AI.Runtime.Contracts;

/// <summary>在已有会话上启动下一轮 Run。</summary>
public sealed record ExistingSessionRunRequest(
    string SessionId,
    string RunIdempotencyKey,
    NextTurnSelection? TurnOverride = null,
    ContentBlock[]? InputOverride = null,
    int? ExpectedSelectionVersion = null,
    IReadOnlyList<HostProviderConfig>? ProviderConfigs = null,
    /// <summary>
    /// 宿主界面语言，例如 <c>zh-CN</c>。省略、null 或空白时，Runtime 使用操作系统界面语言。
    /// </summary>
    string? Language = null,
    /// <summary>
    /// 宿主项目 Id。续跑时用于恢复当前工作区的记忆作用域。
    /// </summary>
    string? ProjectId = null,
    /// <summary>
    /// 当前工作区真实路径。省略时沿用 Runtime 启动目录。
    /// </summary>
    string? WorkspacePath = null);
