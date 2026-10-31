using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Protocol;

namespace Madorin.AI.Multimedia.Core.CommandLine;

/// <summary>联网模型查询的连接参数与总预算，不读取生成请求文件。</summary>
public sealed record ModelListOptions(Uri Endpoint, string ApiKey)
{
    /// <summary>校验调用关联、认证、地址边界并设置本次查询预算。</summary>
    public static ModelListOptions Parse(string[] args, string defaultApiUrl, SkillInvocation invocation, bool requireApiKey = true, int defaultTimeout = 300)
    {
        var options = CommandLineParser.Parse(args,
            new HashSet<string>(["request-id", "api-url", "api-key", "timeout", "output-format", "verbose"], StringComparer.Ordinal),
            new HashSet<string>(["verbose"], StringComparer.Ordinal));
        invocation.SetTimeout(CommandLineParser.ParsePositiveInt(options.GetValueOrDefault("timeout"), defaultTimeout, "timeout"));
        invocation.CredentialPresent = !string.IsNullOrWhiteSpace(options.GetValueOrDefault("api-key"));
        invocation.Verbose = options.GetValueOrDefault("verbose") == "true";
        if (!RequestIds.IsValidRequestId(options.GetValueOrDefault("request-id")) ||
            options.TryGetValue("output-format", out var format) && format != "json")
        {
            throw new SkillException("invalid_parameter", "必须提供 request-id，输出格式仅支持 json。");
        }

        var apiKey = options.GetValueOrDefault("api-key");
        var url = options.GetValueOrDefault("api-url") ?? defaultApiUrl;
        if ((requireApiKey && string.IsNullOrWhiteSpace(apiKey)) || !Uri.TryCreate(url, UriKind.Absolute, out var endpoint) ||
            !(endpoint.Scheme == Uri.UriSchemeHttps || endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback) ||
            endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0 || endpoint.UserInfo.Length > 0)
        {
            throw new SkillException("invalid_parameter", requireApiKey
                ? "必须提供 api-key 及不含用户信息、查询参数或片段的 HTTPS API 地址；仅本机回环地址允许 HTTP。"
                : "api-url 必须是不含用户信息、查询参数或片段的 HTTPS API 地址；仅本机回环地址允许 HTTP。");
        }

        return new ModelListOptions(endpoint, apiKey ?? string.Empty);
    }
}
