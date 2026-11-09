using System.Runtime.CompilerServices;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Madorin.AI.Runtime.Providers.Abstractions;

/// <summary>Builds the shared MAF Skills context provider for one immutable Invocation snapshot.</summary>
public static class MafSkillsAgentFactory
{
    public static async IAsyncEnumerable<MafProviderResponseUpdate> CreateResponseStream(
        IChatClient chatClient,
        RuntimeProviderRequest request,
        ChatMessage[] messages,
        ChatOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(options);

        ApplyRequestControl(options, request.RequestControl);

        var agentOptions = new ChatClientAgentOptions
        {
            Id = request.AgentId,
            ChatOptions = options,
            UseProvidedChatClientAsIs = true
        };
        if (CreateSkillsProvider(request.Skills) is { } skillsProvider)
        {
            agentOptions.AIContextProviders = [skillsProvider];
        }

        var agent = chatClient.AsAIAgent(agentOptions);
        using var outputMapper = new ProviderOutputContentMapper(request);
        if (request.UseNonStreamingResponse)
        {
            // 非流式：一次 HTTP 请求拿到完整响应。所有 Provider 都经由这里调用 MAF，
            // 因此 OpenAI 系、Anthropic、DeepSeek、Kimi 及其派生 Provider 都走同一条路径。
            var response = await agent.RunAsync(messages, cancellationToken: ct).ConfigureAwait(false);
            var rawContents = CollectNonStreamingContents(response);
            var responseContents = await outputMapper.MapAsync(
                    rawContents,
                    terminal: true,
                    ct)
                .ConfigureAwait(false);
            outputMapper.EnsureCompleted();
            yield return new MafProviderResponseUpdate(
                (response.FinishReason ?? InferNonStreamingFinishReason(rawContents))?.ToString(),
                responseContents);
            yield break;
        }

        await foreach (var update in agent.RunStreamingAsync(
                           messages,
                           cancellationToken: ct).ConfigureAwait(false))
        {
            var contents = await outputMapper.MapAsync(update.Contents.ToArray(), update.FinishReason is not null, ct)
                .ConfigureAwait(false);
            yield return new MafProviderResponseUpdate(
                update.FinishReason?.ToString(),
                contents);
        }
        outputMapper.EnsureCompleted();
    }

    /// <summary>
    /// 把非流式响应展开为与流式更新相同的内容序列：先按顺序放入各条消息的内容，
    /// 再在消息中没有用量内容时补上响应级用量，使适配器的事件映射逻辑无需区分两种模式。
    /// </summary>
    private static AIContent[] CollectNonStreamingContents(AgentResponse response)
    {
        var contents = response.Messages.SelectMany(static message => message.Contents).ToList();
        if (response.Usage is { } usage && !contents.Any(static content => content is UsageContent))
        {
            contents.Add(new UsageContent(usage));
        }

        return [.. contents];
    }

    /// <summary>
    /// 非流式响应缺少结束原因时推断结束原因。
    /// 非流式响应是完整接收并解析过的文档；OpenAI Responses API 在 completed 状态下不提供结束原因，
    /// 截断和内容过滤会映射为 Length / ContentFilter，不会走到这里。
    /// 响应里带错误内容时不推断，交由适配器按协议错误处理。
    /// </summary>
    private static ChatFinishReason? InferNonStreamingFinishReason(AIContent[] contents)
    {
        if (contents.Any(static content => content is ErrorContent))
        {
            return null;
        }

        return contents.Any(static content => content is FunctionCallContent)
            ? ChatFinishReason.ToolCalls
            : ChatFinishReason.Stop;
    }

    /// <summary>
    /// Single source of truth for applying <see cref="ExpertRequestControl"/> to Microsoft.Extensions.AI <see cref="ChatOptions"/>.
    ///
    /// Mapping (cross-checked against latest official docs of the major providers as of 2025/2026):
    /// - temperature (0.0-2.0)
    /// - max_tokens / max_output_tokens
    /// - top_p (0.0-1.0]
    /// - top_k (provider dependent)
    /// - stop / stop_sequences
    /// - frequency_penalty / presence_penalty (-2.0 to 2.0)
    /// - seed (for supported providers/models)
    ///
    /// Unsupported parameters for a given provider are simply ignored by the underlying SDK / API.
    /// Stream preference is handled at a higher layer (HTTP sync endpoint forces Stream=false).
    /// </summary>
    public static void ApplyRequestControl(ChatOptions options, ExpertRequestControl? control)
    {
        if (control is null)
        {
            return;
        }

        control.Validate();

        if (control.Temperature is { } temperature)
        {
            options.Temperature = (float)temperature;
        }

        if (control.MaxTokens is { } maxTokens)
        {
            options.MaxOutputTokens = maxTokens;
        }

        if (control.TopP is { } topP)
        {
            options.TopP = (float)topP;
        }

        if (control.TopK is { } topK)
        {
            options.TopK = topK;
        }

        if (control.StopSequences is { Length: > 0 } stopSequences)
        {
            options.StopSequences = [.. stopSequences];
        }

        if (control.FrequencyPenalty is { } frequencyPenalty)
        {
            options.FrequencyPenalty = (float)frequencyPenalty;
        }

        if (control.PresencePenalty is { } presencePenalty)
        {
            options.PresencePenalty = (float)presencePenalty;
        }

        if (control.Seed is { } seed)
        {
            options.Seed = seed;
        }

        // Stream preference is honored at higher layers (HTTP sync forces false).
        // Individual provider adapters may use it to decide between streaming/non-streaming calls when supported.
    }

    public static AgentSkillsProvider? CreateSkillsProvider(SkillsInvocationContext? context)
    {
        if (context is null)
        {
            return null;
        }

        var options = new AgentSkillsProviderOptions
        {
            // Runtime owns approval and routes execution through builtin.skill.script.run.
            ScriptApproval = false,
            DisableCaching = true
        };
        var scriptRunner = context.ScriptInvoker is null
            ? null
            : (AgentFileSkillScriptRunner)((skill, script, arguments, serviceProvider, ct) =>
                RunScriptThroughRuntimeAsync(context, skill, script, arguments, serviceProvider, ct));

        if (context.IsHostMode)
        {
            // Host mode: always create a live Provider. Loadable is resolved dynamically per
            // GetSkillsAsync so same-turn search→load and multi-round activation both work.
            var builder = new AgentSkillsProviderBuilder()
                .UseSource(new HostModeLoadableSkillsSource(context, scriptRunner))
                .UseOptions(providerOptions =>
                {
                    providerOptions.ScriptApproval = options.ScriptApproval;
                    providerOptions.DisableCaching = options.DisableCaching;
                    if (options.SkillsInstructionPrompt is not null)
                    {
                        providerOptions.SkillsInstructionPrompt = options.SkillsInstructionPrompt;
                    }
                });
            if (scriptRunner is not null)
            {
                builder.UseFileScriptRunner(scriptRunner);
            }

            return builder.Build();
        }

        // Independent mode: paths must be non-empty (existing behavior, zero regression).
        if (context.SkillMaterializedPaths is not { Length: > 0 } paths)
        {
            return null;
        }

        return new AgentSkillsProvider(
            paths,
            scriptRunner,
            fileOptions: null,
            options,
            loggerFactory: null);
    }

    /// <summary>
    /// Resolves materialized paths for the current host-mode Loadable set.
    /// Progressive: Activation ∩ Allowlist (optional Initial SkillNames).
    /// Direct: Allowlist names with resolvable catalog/scope paths (no activation ceiling).
    /// </summary>
    public static string[] ResolveHostLoadableMaterializedPaths(SkillsInvocationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var loadableNames = SkillDiscoveryModes.IsDirect(context.SkillDiscoveryMode)
            ? ResolveDirectLoadableNames(context)
            : ResolveProgressiveLoadableNames(context);

        if (loadableNames.Length == 0)
        {
            return [];
        }

        var paths = new List<string>(loadableNames.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in loadableNames)
        {
            if (!TryResolveMaterializedPath(context, name, out var path)
                || !seen.Add(path))
            {
                continue;
            }

            paths.Add(path);
        }

        return [.. paths];
    }

    /// <summary>
    /// Validates that <paramref name="name"/> is loadable in host mode and resolves its path.
    /// Progressive requires activation; direct only requires allowlist + resolvable path.
    /// Independent mode always returns the path map hit.
    /// </summary>
    public static bool TryResolveLoadPath(
        SkillsInvocationContext context,
        string name,
        out string materializedPath,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(context);
        materializedPath = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Skill name is required.";
            return false;
        }

        var normalized = name.Trim();
        if (context.IsHostMode)
        {
            if (!SkillActivationStore.IsAllowed(normalized, context.AllowlistSkillIds))
            {
                error = $"Skill '{normalized}' is outside the current allowlist.";
                return false;
            }

            var isDirect = SkillDiscoveryModes.IsDirect(context.SkillDiscoveryMode);
            if (!isDirect
                && (context.ActivationStore is null
                    || !context.ActivationStore.IsLoadable(normalized, context.AllowlistSkillIds)))
            {
                error = $"Skill '{normalized}' is not activated. Call skill.search first.";
                return false;
            }
        }

        if (!TryResolveMaterializedPath(context, normalized, out materializedPath))
        {
            error = $"Skill '{normalized}' has no materialized path in the current catalog.";
            return false;
        }

        return true;
    }

    private static string[] ResolveProgressiveLoadableNames(SkillsInvocationContext context)
    {
        var loadableNames = context.ActivationStore?.GetLoadableNames(context.AllowlistSkillIds)
            ?? [];
        if (loadableNames.Length == 0
            && context.SkillNames is { Length: > 0 }
            && context.SkillMaterializedPaths is { Length: > 0 })
        {
            // Optional InitialSkills already projected into SkillNames/paths.
            loadableNames = context.SkillNames
                .Where(name => SkillActivationStore.IsAllowed(name, context.AllowlistSkillIds))
                .ToArray();
        }

        return loadableNames;
    }

    /// <summary>
    /// Direct Loadable names = Allowlist (null → all catalog names; [] → none),
    /// without consuming progressive activation slots.
    /// Path resolvability is checked by the caller when building path lists.
    /// </summary>
    private static string[] ResolveDirectLoadableNames(SkillsInvocationContext context)
    {
        var allowlist = context.AllowlistSkillIds;
        if (allowlist is { Length: 0 })
        {
            return [];
        }

        if (allowlist is { Length: > 0 })
        {
            return allowlist
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Select(static name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        // null allowlist = undeclared → all known catalog names (use with care).
        if (context.CatalogMaterializedPathsByName is { Count: > 0 } catalog)
        {
            return catalog.Keys
                .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        if (context.SkillNames is { Length: > 0 })
        {
            return context.SkillNames
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Select(static name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return [];
    }

    private static bool TryResolveMaterializedPath(
        SkillsInvocationContext context,
        string name,
        out string path)
    {
        path = string.Empty;
        if (context.CatalogMaterializedPathsByName is not null
            && context.CatalogMaterializedPathsByName.TryGetValue(name, out var catalogPath)
            && !string.IsNullOrWhiteSpace(catalogPath))
        {
            path = catalogPath;
            return true;
        }

        if (context.SkillNames is { Length: > 0 }
            && context.SkillMaterializedPaths is { Length: > 0 })
        {
            var count = Math.Min(context.SkillNames.Length, context.SkillMaterializedPaths.Length);
            for (var i = 0; i < count; i++)
            {
                if (string.Equals(context.SkillNames[i], name, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(context.SkillMaterializedPaths[i]))
                {
                    path = context.SkillMaterializedPaths[i];
                    return true;
                }
            }
        }

        // Lazy resolve from bound source roots (scope from activation when present).
        return TryResolveFromScopeRoots(context, name, out path);
    }

    private static bool TryResolveFromScopeRoots(
        SkillsInvocationContext context,
        string name,
        out string path)
    {
        path = string.Empty;
        if (context.ScopeSourceRoots is not { Count: > 0 })
        {
            return false;
        }

        string? preferredScope = null;
        if (context.ActivationStore is not null)
        {
            foreach (var entry in context.ActivationStore.Snapshot())
            {
                if (string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    preferredScope = entry.Scope;
                    break;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(preferredScope))
        {
            foreach (var root in context.ScopeSourceRoots)
            {
                if (string.Equals(root.ScopeId, preferredScope, StringComparison.OrdinalIgnoreCase)
                    && TryResolveSkillDirectory(root.DirectoryPath, name, out path))
                {
                    return true;
                }
            }
        }

        // Shadow order follows catalog source order: workspace > user-global > explicit.
        foreach (var root in context.ScopeSourceRoots)
        {
            if (TryResolveSkillDirectory(root.DirectoryPath, name, out path))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveSkillDirectory(string sourceRoot, string skillName, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
        {
            return false;
        }

        var direct = Path.Combine(sourceRoot, skillName);
        if (Directory.Exists(direct)
            && File.Exists(Path.Combine(direct, "SKILL.md")))
        {
            path = direct;
            return true;
        }

        foreach (var directory in Directory.EnumerateDirectories(sourceRoot))
        {
            var folderName = Path.GetFileName(directory);
            if (!string.Equals(folderName, skillName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (File.Exists(Path.Combine(directory, "SKILL.md")))
            {
                path = directory;
                return true;
            }
        }

        return false;
    }

    private static async Task<object?> RunScriptThroughRuntimeAsync(
        SkillsInvocationContext context,
        AgentSkill skill,
        AgentFileSkillScript script,
        JsonElement? arguments,
        IServiceProvider? serviceProvider,
        CancellationToken ct)
    {
        _ = serviceProvider;
        if (context.ScriptInvoker is null)
        {
            throw new InvalidOperationException("The Runtime Skills script invoker is unavailable.");
        }

        if (context.IsHostMode)
        {
            if (!TryResolveLoadPath(context, skill.Frontmatter.Name, out _, out var error))
            {
                throw new InvalidOperationException(error ?? "Skill is not loadable.");
            }
        }

        var values = arguments is { } argumentValue
            && argumentValue.ValueKind is JsonValueKind.Array
            ? argumentValue.EnumerateArray()
                .Select(static value => value.ValueKind is JsonValueKind.String
                    ? value.GetString() ?? string.Empty
                    : value.GetRawText())
                .ToArray()
            : [];
        var scriptName = script.Name;
        if (skill is AgentFileSkill fileSkill)
        {
            var relative = Path.GetRelativePath(fileSkill.Path, script.FullPath);
            if (!Path.IsPathRooted(relative)
                && !relative.Equals("..", StringComparison.Ordinal)
                && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                scriptName = relative.Replace(Path.DirectorySeparatorChar, '/');
            }
        }

        var result = await context.ScriptInvoker(
            new RuntimeSkillScriptInvocation(
                skill.Frontmatter.Name,
                scriptName,
                context.CatalogVersion,
                values,
                context.RunId ?? throw new InvalidOperationException("Skills RunId is missing."),
                context.SessionId ?? throw new InvalidOperationException("Skills SessionId is missing."),
                context.AgentId ?? throw new InvalidOperationException("Skills AgentId is missing."),
                context.InvocationId ?? throw new InvalidOperationException("Skills InvocationId is missing."),
                context.ParentAgentId,
                context.ParentInvocationId,
                context.WorkStepId,
                context.PlanVersion),
            ct).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Error ?? "Skill script execution failed.");
        }

        return result.OutputJson;
    }
}

public sealed record MafProviderResponseUpdate(
    string? FinishReason,
    AIContent[] Contents);
