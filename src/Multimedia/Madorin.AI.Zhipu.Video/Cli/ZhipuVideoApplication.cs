using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.CommandLine;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;
using Madorin.AI.Zhipu.Video.Metadata;
using Madorin.AI.Zhipu.Video.Provider;
using Madorin.AI.Zhipu.Video.Requests;

namespace Madorin.AI.Zhipu.Video.Cli;

internal sealed class ZhipuVideoApplication(HttpClient client, TimeSpan? pollInterval = null)
{
    public async Task<int> RunAsync(string[] args, TextWriter output, string workingDirectory, Stream? controlInput = null)
    {
        var writer = new ProtocolWriter(output);
        var paths = new WorkspacePaths(workingDirectory);
        await using var invocation = new SkillInvocation();
        using var artifacts = new VideoArtifactStore(paths);
        var id = RequestIds.FindRequestId(args);
        invocation.ResponseId = RequestIds.IsValidRequestId(id) ? id : null;
        if (args.Length > 0 && args[0] is "query" or "download") invocation.TaskId = ZhipuVideoTaskIds.Find(args[1..]);
        invocation.ListenForCancellation(controlInput);
        invocation.TryRun = args.Length > 0 && args[0] == "generate" && args.Any(value => value is "--try-run" or "--try-run=true");
        try
        {
            if (args.Length == 0) throw new SkillException("invalid_parameter", "命令必需。支持 metadata、list-models、version、generate、query、download。");
            if (id is not null && !RequestIds.IsValidRequestId(id)) throw new SkillException("invalid_parameter", "request-id 格式无效。");
            JsonObject response = args[0] switch
            {
                "generate" => await GenerateAsync(args, invocation, paths, artifacts),
                "list-models" => List(args, id),
                "query" => await QueryAsync(args, invocation, paths),
                "download" => await DownloadAsync(args, invocation, paths, artifacts),
                _ => Local(args, id)
            };
            if (invocation.CommitTerminal() is { } termination) return WriteFailure(termination, string.Empty);
            var exitCode = response["status"]?.GetValue<string>() == "failed"
                ? writer.Write(response, ProtocolWriter.ExitCode(response["error"]?["code"]?.GetValue<string>() ?? "provider_error"))
                : writer.Write(response);
            artifacts.Commit();
            return exitCode;
        }
        catch (SkillException exception)
        {
            var code = invocation.CommitTerminal(exception.Code);
            if (invocation.TryRun && code == "invalid_parameter") return writer.Write(SkillResponses.Invalid("zhipu", invocation, exception.Code, exception.Message), 1);
            return WriteFailure(code!, exception.Message, exception.ProviderCode);
        }
        catch (OperationCanceledException) { return WriteFailure(invocation.HostCancelled ? "cancelled" : "timeout", string.Empty); }
        catch (HttpRequestException) { return WriteFailure("network_error", "连接智谱失败。"); }
        catch (Exception) { return WriteFailure("internal_error", "发生未预期错误。"); }

        int WriteFailure(string code, string message, string? providerCode = null)
        {
            var finalCode = invocation.CommitTerminal(code)!;
            var finalMessage = finalCode switch { "timeout" => "调用超时。", "cancelled" => "调用已取消。", _ => message };
            var failure = SkillResponses.Failure("zhipu", invocation, finalCode, finalMessage, finalCode == code ? providerCode : null);
            if (args.Length > 0 && args[0] is "query" or "download" && finalCode is "network_error" or "timeout" or "rate_limit_exceeded" or "task_not_ready")
            {
                failure["error"]!["retryable"] = JsonValue.Create(true);
            }
            return writer.Write(failure, ProtocolWriter.ExitCode(finalCode));
        }
    }

    private async Task<JsonObject> GenerateAsync(string[] args, SkillInvocation invocation, WorkspacePaths paths, VideoArtifactStore artifacts)
    {
        invocation.SetTimeout(300);
        var options = await new VideoGenerationOptionsFactory(paths).CreateAsync(args[1..], invocation);
        var imageUrl = options.TryRun ? options.RemoteImageUrl : await VideoImageMaterializer.MaterializeAsync(options, paths, invocation.CancellationToken);
        var request = ZhipuVideoClient.Map(options, imageUrl);
        if (options.TryRun) return VideoTryRunResponse.Build(options, invocation, ZhipuVideoClient.Preview(request));
        if (!options.NoWait) VideoValidator.EnsureTools();
        var provider = new ZhipuVideoClient(client, pollInterval);
        var submitted = await provider.SubmitAsync(options, request, invocation);
        if (options.NoWait) return submitted;
        var job = await provider.WaitAsync(options.Endpoint, invocation.TaskId!, options.ApiKey, invocation);
        if (job.Status != "SUCCESS") throw new SkillException("provider_error", "智谱视频生成失败。");
        var videos = await provider.DownloadAsync(job, options.OutputDirectory!, invocation, artifacts);
        return SkillResponses.Generated("zhipu", "video", invocation, videos);
    }

    private async Task<JsonObject> QueryAsync(string[] args, SkillInvocation invocation, WorkspacePaths paths)
    {
        var options = await TaskOperationOptionsFactory.CreateAsync(args[1..], false, paths, invocation);
        var provider = new ZhipuVideoClient(client, pollInterval);
        var job = options.Wait
            ? await provider.WaitAsync(options.Endpoint, options.TaskId, options.ApiKey, invocation)
            : await provider.QueryOnceAsync(options.Endpoint, options.TaskId, options.ApiKey, invocation);
        var status = job.Status switch { "SUCCESS" => "succeeded", "FAIL" => "failed", _ => "pending" };
        JsonObject? error = status == "failed" ? JsonProtocol.Obj(("code", JsonProtocol.S("provider_error")), ("message", JsonProtocol.S("智谱视频生成失败。")), ("retryable", JsonProtocol.B(false))) : null;
        return ZhipuVideoClient.TaskView(job.TaskId, status, error, invocation);
    }

    private async Task<JsonObject> DownloadAsync(string[] args, SkillInvocation invocation, WorkspacePaths paths, VideoArtifactStore artifacts)
    {
        var options = await TaskOperationOptionsFactory.CreateAsync(args[1..], true, paths, invocation);
        var provider = new ZhipuVideoClient(client, pollInterval);
        var job = await provider.QueryOnceAsync(options.Endpoint, options.TaskId, options.ApiKey, invocation);
        if (job.Status == "PROCESSING") throw new SkillException("task_not_ready", "视频尚未完成。");
        if (job.Status != "SUCCESS") throw new SkillException("provider_error", "智谱视频生成失败。");
        var videos = await provider.DownloadAsync(job, options.OutputDirectory!, invocation, artifacts);
        return SkillResponses.Generated("zhipu", "video", invocation, videos);
    }

    private static JsonObject List(string[] args, string? id)
    {
        var allowed = new HashSet<string>(["request-id", "api-url", "api-key", "timeout", "output-format", "verbose"], StringComparer.Ordinal);
        var options = CommandLineParser.Parse(args[1..], allowed, new HashSet<string>(["verbose"], StringComparer.Ordinal));
        if (id is null || !options.TryGetValue("api-key", out var key) || string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl) ||
            options.TryGetValue("output-format", out var format) && format != "json" ||
            options.TryGetValue("api-url", out var address) && !VideoGenerationOptionsFactory.TryEndpoint(address, out _))
        {
            throw new SkillException("invalid_parameter", "必须提供 request-id、api-key 和有效 HTTPS API 地址，输出格式仅支持 json。");
        }

        CommandLineParser.ParsePositiveInt(options.GetValueOrDefault("timeout"), 300, "timeout");
        throw new SkillException("provider_error", "智谱官方 API 未提供通用模型目录端点。", "model_catalog_unsupported");
    }

    private static JsonObject Local(string[] args, string? id)
    {
        var options = CommandLineParser.Parse(args[1..], new HashSet<string>(["request-id", "output-format", "verbose"], StringComparer.Ordinal), new HashSet<string>(["verbose"], StringComparer.Ordinal));
        if (id is null || options.TryGetValue("output-format", out var format) && format != "json") throw new SkillException("invalid_parameter", "必须提供 request-id，输出格式仅支持 json。");
        return args[0] switch
        {
            "metadata" => ZhipuVideoMetadata.BuildMetadata(id),
            "version" => ZhipuVideoMetadata.BuildVersion(id),
            _ => throw new SkillException("invalid_parameter", "未知命令。")
        };
    }
}
