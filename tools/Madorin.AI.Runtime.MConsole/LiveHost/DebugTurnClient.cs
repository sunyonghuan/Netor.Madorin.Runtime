using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;

using Madorin.AI.Runtime.MConsole.HostClient;

namespace Madorin.AI.Runtime.MConsole.LiveHost;

/// <summary>用当前厂商、模型和模式向已连接的 CLI Runtime 提交一轮。</summary>
public sealed class DebugTurnClient
{
    public required HostClientSession Host { get; init; }

    public required DebugEventPump Pump { get; init; }

    public required DebugConfigSnapshot Config { get; init; }

    public required string ExecutablePath { get; init; }

    public required string Workspace { get; init; }

    public RuntimeMode Mode { get; set; } = RuntimeMode.Expert;

    public string ProviderId { get; set; } = string.Empty;

    public string ModelId { get; set; } = string.Empty;

    public string? SessionId { get; set; }

    public bool SelectionDirty { get; set; }

    public async Task<string> SendAsync(string input, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ProviderId) || string.IsNullOrWhiteSpace(ModelId))
        {
            throw new InvalidOperationException("还没有厂商或模型。先用 /provider 和 /model 指定。");
        }

        await ApplySelectionAsync(cancellationToken);
        var providers = Config.HostConfigs;
        string runId;
        if (string.IsNullOrWhiteSpace(SessionId))
        {
            var created = new NewSessionRunRequest(
                $"debug-session-{Guid.NewGuid():N}",
                $"debug-run-{Guid.NewGuid():N}",
                Mode,
                DebugSelectionFactory.Create(Mode, Config, ProviderId, ModelId, 1),
                [new TextContentBlock(input)],
                ProviderConfigs: providers,
                Language: "zh-CN");
            runId = await Host.Client.StartNewSessionRunAsync(created, cancellationToken);
            SelectionDirty = false;
        }
        else
        {
            var continued = new ExistingSessionRunRequest(
                SessionId,
                $"debug-run-{Guid.NewGuid():N}",
                InputOverride: [new TextContentBlock(input)],
                ProviderConfigs: providers,
                Language: "zh-CN");
            runId = await Host.Client.StartExistingSessionRunAsync(continued, cancellationToken);
        }

        Pump.Expect(runId);
        return runId;
    }

    public async Task CancelAsync(string runId, CancellationToken cancellationToken) =>
        await Host.Client.CancelRunAsync(runId, "debug console cancel", cancellationToken);

    private async Task ApplySelectionAsync(CancellationToken cancellationToken)
    {
        if (!SelectionDirty || string.IsNullOrWhiteSpace(SessionId))
        {
            return;
        }

        var current = await Host.Client.GetSessionAsync(SessionId, cancellationToken);
        if (current.Mode != Mode)
        {
            throw new InvalidOperationException("当前会话的模式已经固定。输入 /new 后再换模式。");
        }

        var expected = current.Selection?.SelectionVersion ?? 0;
        var selection = DebugSelectionFactory.Create(Mode, Config, ProviderId, ModelId, expected + 1);
        await Host.Client.UpdateSessionSelectionAsync(
            new SessionSelectionUpdateParameters(
                SessionId,
                expected,
                selection,
                ProviderConfigs: Config.HostConfigs),
            cancellationToken);
        SelectionDirty = false;
    }
}
