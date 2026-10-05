using System.Text;
using System.Text.Json;

using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.MConsole.LiveHost;

/// <summary>配置里的一个厂商。展示文本不包含密钥。</summary>
public sealed record DebugProviderInfo(
    string Name,
    string Protocol,
    IReadOnlyList<string> Models);

/// <summary>配置里的一个 Agent。系统提示只用于组装 Selection，不打印密钥。</summary>
public sealed record DebugAgentInfo(
    string Id,
    string SystemPrompt,
    string? Provider,
    string? Model);

/// <summary>
/// 从当前 CLI 的个人配置读取厂商和 Agent。密钥只留在宿主请求里，不进入输出。
/// </summary>
public sealed class DebugConfigSnapshot
{
    public const string DefaultPrompt = "你是 Madorin 调试台中的代理。按当前模式完成用户请求。";

    private readonly IReadOnlyList<HostProviderConfig> _hostConfigs;

    private DebugConfigSnapshot(
        string path,
        string? defaultProvider,
        string? defaultModel,
        string defaultAgentId,
        IReadOnlyList<DebugProviderInfo> providers,
        IReadOnlyList<DebugAgentInfo> agents,
        IReadOnlyList<HostProviderConfig> hostConfigs)
    {
        Path = path;
        DefaultProvider = defaultProvider;
        DefaultModel = defaultModel;
        DefaultAgentId = defaultAgentId;
        Providers = providers;
        Agents = agents;
        _hostConfigs = hostConfigs;
    }

    public string Path { get; }

    public string? DefaultProvider { get; }

    public string? DefaultModel { get; }

    public string DefaultAgentId { get; }

    public IReadOnlyList<DebugProviderInfo> Providers { get; }

    public IReadOnlyList<DebugAgentInfo> Agents { get; }

    public IReadOnlyList<HostProviderConfig> HostConfigs => _hostConfigs;

    public string Describe()
    {
        var builder = new StringBuilder();
        builder.Append("配置: ").Append(Path);
        builder.Append("\n默认厂商: ").Append(DefaultProvider ?? "(无)");
        builder.Append("  默认模型: ").Append(DefaultModel ?? "(无)");
        builder.Append("  默认 Agent: ").Append(DefaultAgentId);
        if (Providers.Count == 0)
        {
            builder.Append("\n没有读到厂商。请先用 madorin config init 写好个人配置。");
            return builder.ToString();
        }

        foreach (var provider in Providers)
        {
            builder.Append("\n- ").Append(provider.Name)
                .Append(" [").Append(provider.Protocol).Append("] ");
            builder.Append(provider.Models.Count == 0
                ? "(无模型)"
                : string.Join(", ", provider.Models));
        }

        return builder.ToString();
    }

    public static DebugConfigSnapshot Load(string configDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        var directory = System.IO.Path.GetFullPath(configDirectory);
        var configPath = System.IO.Path.Combine(directory, "config.json");
        var providers = new List<DebugProviderInfo>();
        var hostConfigs = new List<HostProviderConfig>();
        string? defaultProvider = null;
        string? defaultModel = null;
        var defaultAgent = "default";
        if (File.Exists(configPath))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = document.RootElement;
            defaultProvider = ReadString(root, "defaultProvider");
            defaultModel = ReadString(root, "defaultModel");
            defaultAgent = ReadString(root, "defaultAgent") ?? "default";
            if (root.TryGetProperty("providers", out var providerArray)
                && providerArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in providerArray.EnumerateArray())
                {
                    AddProvider(item, providers, hostConfigs);
                }
            }
        }

        var agents = LoadAgents(System.IO.Path.Combine(directory, "agents"));
        return new DebugConfigSnapshot(
            configPath,
            defaultProvider,
            defaultModel,
            defaultAgent,
            providers,
            agents,
            hostConfigs);
    }

    private static void AddProvider(
        JsonElement item,
        List<DebugProviderInfo> providers,
        List<HostProviderConfig> hostConfigs)
    {
        var name = ReadString(item, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var protocol = (ReadString(item, "protocol") ?? string.Empty).Trim().ToLowerInvariant();
        var models = ReadStrings(item, "models");
        providers.Add(new DebugProviderInfo(
            name,
            protocol,
            models));
        hostConfigs.Add(new HostProviderConfig(
            name,
            protocol,
            ReadString(item, "baseUrl") ?? string.Empty,
            ReadString(item, "apiKey") ?? string.Empty,
            ReadString(item, "authenticationHeader"),
            ReadString(item, "authenticationScheme"),
            ReadString(item, "userAgent"),
            ReadString(item, "apiVersion"),
            models,
            ReadString(item, "configurationVersion")));
    }

    private static List<DebugAgentInfo> LoadAgents(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var agents = new List<DebugAgentInfo>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var id = ReadString(root, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            agents.Add(new DebugAgentInfo(
                id,
                ReadString(root, "systemPrompt") ?? string.Empty,
                ReadString(root, "provider"),
                ReadString(root, "model")));
        }

        return agents;
    }

    private static string[] ReadStrings(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(static entry => entry.ValueKind == JsonValueKind.String)
            .Select(static entry => entry.GetString() ?? string.Empty)
            .Where(static entry => entry.Length > 0)
            .ToArray();
    }

    private static string? ReadString(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
