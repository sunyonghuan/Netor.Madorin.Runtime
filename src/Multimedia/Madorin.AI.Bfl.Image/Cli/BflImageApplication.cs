using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.CommandLine;
using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Files;
using Madorin.AI.Multimedia.Core.Protocol;
using Madorin.AI.Bfl.Image.Metadata;
using Madorin.AI.Bfl.Image.Provider;
using Madorin.AI.Bfl.Image.Requests;

namespace Madorin.AI.Bfl.Image.Cli;

internal sealed class BflImageApplication(HttpClient client)
{
    public async Task<int> RunAsync(string[] args, TextWriter output, string workingDirectory, Stream? controlInput = null)
    {
        var writer = new ProtocolWriter(output);
        var paths = new WorkspacePaths(workingDirectory);
        await using var invocation = new SkillInvocation();
        using var artifacts = new ArtifactStore(paths);
        var id = RequestIds.FindRequestId(args);
        invocation.ResponseId = RequestIds.IsValidRequestId(id) ? id : null;
        invocation.ListenForCancellation(controlInput);
        invocation.TryRun = args.Length > 0 && args[0] == "generate" && args.Any(value => value is "--try-run" or "--try-run=true");
        try
        {
            if (args.Length == 0)
            {
                throw new SkillException("invalid_parameter", "命令必需。支持 metadata、list-models、version、generate。");
            }

            if (id is not null && !RequestIds.IsValidRequestId(id))
            {
                throw new SkillException("invalid_parameter", "request-id 格式无效。");
            }

            JsonObject response;
            if (args[0] == "generate")
            {
                invocation.SetTimeout(300);
                var options = await new ImageGenerationOptionsFactory(paths).CreateAsync(args[1..], invocation);
                invocation.ThrowIfCancellationRequested();
                var request = BflImageClient.Map(options);
                if (options.TryRun)
                {
                    response = ImageTryRunResponse.Build(options, invocation, BflImageClient.Preview(request));
                }
                else
                {
                    var generated = await new BflImageClient(client).GenerateAsync(options, request, invocation, artifacts);
                    response = SkillResponses.Generated("bfl", "image", invocation, generated);
                }
            }
            else if (args[0] == "list-models")
            {
                ValidateModelList(args, id);
                throw new SkillException("provider_error", "BFL 官方 API 未提供通用模型目录端点。", "model_catalog_unsupported");
            }
            else
            {
                ValidateLocalCommand(args, id);
                response = args[0] switch
                {
                    "metadata" => BflImageMetadata.BuildMetadata(id),

                    "version" => BflImageMetadata.BuildVersion(id),
                    _ => throw new SkillException("invalid_parameter", "未知命令。")
                };
            }

            if (invocation.CommitTerminal() is { } termination)
            {
                return WriteFailure(termination, string.Empty);
            }

            var exitCode = writer.Write(response);
            artifacts.Commit();
            return exitCode;
        }
        catch (SkillException exception)
        {
            var code = invocation.CommitTerminal(exception.Code);
            if (invocation.TryRun && code == "invalid_parameter")
            {
                return writer.Write(SkillResponses.Invalid("bfl", invocation, exception.Code, exception.Message), 1);
            }

            return WriteFailure(code!, exception.Message, exception.ProviderCode);
        }
        catch (OperationCanceledException)
        {
            return WriteFailure(invocation.HostCancelled ? "cancelled" : "timeout", string.Empty);
        }
        catch (HttpRequestException)
        {
            return WriteFailure("network_error", "连接 BFL 失败。");
        }
        catch (Exception)
        {
            return WriteFailure("internal_error", "发生未预期错误。");
        }

        int WriteFailure(string code, string message, string? providerCode = null)
        {
            var finalCode = invocation.CommitTerminal(code)!;
            var finalMessage = finalCode switch
            {
                "timeout" => "调用超时。",
                "cancelled" => "调用已取消。",
                _ => message
            };
            return writer.Write(SkillResponses.Failure("bfl", invocation, finalCode, finalMessage, finalCode == code ? providerCode : null), ProtocolWriter.ExitCode(finalCode));
        }
    }

    private static void ValidateModelList(string[] args, string? id)
    {
        var allowed = new HashSet<string>(["request-id", "api-url", "api-key", "timeout", "output-format", "verbose"], StringComparer.Ordinal);
        var switches = new HashSet<string>(["verbose"], StringComparer.Ordinal);
        var options = CommandLineParser.Parse(args[1..], allowed, switches);
        if (id is null || !options.TryGetValue("api-key", out var key) || string.IsNullOrWhiteSpace(key) ||
            options.TryGetValue("output-format", out var format) && format != "json" ||
            options.TryGetValue("api-url", out var address) && (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0))
        {
            throw new SkillException("invalid_parameter", "必须提供 request-id、api-key 和有效 HTTPS API 地址，输出格式仅支持 json。");
        }

        CommandLineParser.ParsePositiveInt(options.GetValueOrDefault("timeout"), 300, "timeout");
    }

    private static void ValidateLocalCommand(string[] args, string? id)
    {
        var allowed = new HashSet<string>(["request-id", "output-format", "verbose"], StringComparer.Ordinal);
        var switches = new HashSet<string>(["verbose"], StringComparer.Ordinal);
        var options = CommandLineParser.Parse(args[1..], allowed, switches);
        if (id is null || options.TryGetValue("output-format", out var format) && format != "json")
        {
            throw new SkillException("invalid_parameter", "必须提供 request-id，输出格式仅支持 json。");
        }
    }
}



