using System.Runtime.CompilerServices;
using System.Text.Json;
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
        await foreach (var update in agent.RunStreamingAsync(
                           messages,
                           cancellationToken: ct).ConfigureAwait(false))
        {
            yield return new MafProviderResponseUpdate(
                update.FinishReason?.ToString(),
                update.Contents.ToArray());
        }
    }

    public static AgentSkillsProvider? CreateSkillsProvider(SkillsInvocationContext? context)
    {
        if (context?.SkillMaterializedPaths is not { Length: > 0 } paths)
        {
            return null;
        }

        var options = new AgentSkillsProviderOptions
        {
            // Runtime owns approval and routes execution through builtin.skill.script.run.
            ScriptApproval = false,
            DisableCaching = true
        };
        return new AgentSkillsProvider(
            paths,
            scriptRunner: context.ScriptInvoker is null
                ? null
                : (skill, script, arguments, serviceProvider, ct) =>
                    RunScriptThroughRuntimeAsync(context, skill, script, arguments, serviceProvider, ct),
            fileOptions: null,
            options,
            loggerFactory: null);
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
