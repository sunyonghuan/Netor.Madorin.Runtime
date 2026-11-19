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
    string? Language = null,
    /// <summary>
    /// 宿主项目 Id，例如 <c>01M6RJCSMGWFP2TFZM2KMQ967G</c>。
    /// Runtime 使用它推导记忆工作区标识 <c>MD5(ProjectId)</c>。
    /// 旧宿主只传 <see cref="WorkspaceId"/> 时回退使用该值。
    /// </summary>
    string? ProjectId = null,
    /// <summary>
    /// 当前工作区真实路径。宿主未传时 Runtime 使用启动目录。
    /// 只用于 Environment 展示，不参与记忆工作区标识计算。
    /// </summary>
    string? WorkspacePath = null);
