using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;

internal enum PermissionMode
{
    Grant,
    DenyWrite
}

internal sealed class PermissionPolicy
{
    private int _mode;

    public PermissionMode Mode
    {
        get => (PermissionMode)Volatile.Read(ref _mode);
        set => Volatile.Write(ref _mode, (int)value);
    }

    public bool ShouldDeny(string toolId) =>
        Mode == PermissionMode.DenyWrite
        && (toolId.Contains("write", StringComparison.OrdinalIgnoreCase)
            || toolId.Contains("delete", StringComparison.OrdinalIgnoreCase)
            || toolId.Contains("process", StringComparison.OrdinalIgnoreCase)
            || toolId.Contains("run_script", StringComparison.OrdinalIgnoreCase));
}

internal sealed record CaseResult(string Name, bool Ok, string Detail);

internal sealed record TurnCapture(
    string SessionId,
    string Text,
    string Terminal,
    long LastGsn,
    IReadOnlyList<string> ToolIds,
    IReadOnlyList<string> FailedTools);

internal static class SmokeSupport
{
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Madorin.AI.Runtime.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    public static string ResolvePublishedRuntime(string repoRoot)
    {
        string[] candidates =
        [
            Path.Combine(repoRoot, "releases", "Madorin-Cli", "madorin-cli", "bin", "madorin-cli.exe"),
            Path.Combine(repoRoot, "releases", "Madorin-Cli", "madorin-cli", "bin", "madorin.exe"),
        ];
        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException("未找到发布产物 madorin-cli.exe，请先执行 build/madorin-cli.publish.ps1。");
    }

    public static (HostProviderConfig Config, string ModelId) LoadProviderFromGlobalConfig()
    {
        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".madorin",
            "config.json");
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException($"缺少全局配置: {configPath}");
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
        var root = doc.RootElement;
        var defaultProvider = root.GetProperty("defaultProvider").GetString()
            ?? throw new InvalidDataException("defaultProvider missing");
        var defaultModel = root.GetProperty("defaultModel").GetString()
            ?? throw new InvalidDataException("defaultModel missing");
        foreach (var provider in root.GetProperty("providers").EnumerateArray())
        {
            var name = provider.GetProperty("name").GetString();
            if (!string.Equals(name, defaultProvider, StringComparison.Ordinal))
            {
                continue;
            }

            return (new HostProviderConfig(
                ProviderId: name!,
                Protocol: provider.GetProperty("protocol").GetString()!,
                BaseUrl: provider.GetProperty("baseUrl").GetString()!,
                ApiKey: provider.GetProperty("apiKey").GetString()!,
                AuthenticationHeader: provider.TryGetProperty("authenticationHeader", out var header)
                    ? header.GetString()
                    : null,
                AuthenticationScheme: provider.TryGetProperty("authenticationScheme", out var scheme)
                    ? scheme.GetString()
                    : null,
                UserAgent: provider.TryGetProperty("userAgent", out var userAgent)
                    ? userAgent.GetString()
                    : null,
                Models: provider.TryGetProperty("models", out var models)
                    ? [.. models.EnumerateArray().Select(static item => item.GetString()!)]
                    : null,
                ConfigurationVersion: provider.TryGetProperty("configurationVersion", out var version)
                    ? version.GetString()
                    : null), defaultModel);
        }

        throw new InvalidDataException($"全局配置中没有 Provider '{defaultProvider}'。");
    }

    public static NextTurnSelection Select(
        string providerId,
        string modelId,
        string prompt,
        string[]? skillIds,
        string[]? allowedTools) =>
        new(
            1,
            RuntimeMode.Expert,
            new DefaultSelection(providerId, modelId),
            new ExpertModeOptions(
                new AgentRef(
                    "host-expert",
                    "1",
                    prompt,
                    providerId,
                    modelId,
                    AllowedToolIds: allowedTools,
                    SkillIds: skillIds,
                    SkillDiscoveryMode: "direct")));

    public static ToolGrant CreateGrant(string workspace, ToolPermissionRequest request)
    {
        var grantId = "host-smoke-" + Guid.NewGuid().ToString("N");
        return new ToolGrant(
            grantId,
            request.RunId ?? string.Empty,
            workspace,
            [workspace],
            [workspace],
            true,
            true,
            true,
            ["dotnet"],
            false,
            new NetworkPolicy(true, []),
            DateTimeOffset.UtcNow.AddHours(2),
            false,
            [],
            AllowedToolIds: [request.ToolId],
            AllowedCallIds: [request.CallId],
            MaximumRisk: ToolRiskLevel.Process,
            AgentId: request.AgentId,
            RootGrantId: grantId,
            ApprovalRequestId: request.ApprovalRequestId);
    }
}
