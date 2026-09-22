using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Contracts;

/// <summary>创建新会话并启动一轮 Run。</summary>
public sealed record NewSessionRunRequest(
    string SessionIdempotencyKey,
    string RunIdempotencyKey,
    RuntimeMode Mode,
    NextTurnSelection Selection,
    ContentBlock[] InitialInput,
    string? WorkspaceId = null,
    IReadOnlyList<HostProviderConfig>? ProviderConfigs = null,
    /// <summary>
    /// 宿主界面语言，例如 <c>zh-CN</c>。省略、null 或空白时，Runtime 使用操作系统界面语言。
    /// </summary>
    string? Language = null);
