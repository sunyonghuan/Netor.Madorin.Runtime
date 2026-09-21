using System;
using System.Text.RegularExpressions;
using Madorin.AI.Runtime.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: Parallelize(Scope = ExecutionScope.ClassLevel, Workers = 0)]

namespace Madorin.AI.Runtime.HostIntegration.Tests;

/// <summary>
/// Expert模式@委派机制单元测试
/// 测试核心的委派语法解析和System Prompt注入逻辑
/// </summary>
[TestClass]
public class ExpertModeDelegationUnitTests
{
    [TestMethod]
    public void TryParseDelegationSyntax_ValidSyntax_ReturnsTrue()
    {
        // Arrange
        var input = "@developer Read the file at /test/file.txt";

        // Act
        var (isDelegation, agentName, task) = TryParseDelegationSyntax(input);

        // Assert
        Assert.IsTrue(isDelegation);
        Assert.AreEqual("developer", agentName);
        Assert.AreEqual("Read the file at /test/file.txt", task);
    }

    [TestMethod]
    public void TryParseDelegationSyntax_WithHyphen_ReturnsTrue()
    {
        // Arrange
        var input = "@code-reviewer Review this PR";

        // Act
        var (isDelegation, agentName, task) = TryParseDelegationSyntax(input);

        // Assert
        Assert.IsTrue(isDelegation);
        Assert.AreEqual("code-reviewer", agentName);
        Assert.AreEqual("Review this PR", task);
    }

    [TestMethod]
    public void TryParseDelegationSyntax_MultilineTask_ReturnsTrue()
    {
        // Arrange
        var input = "@developer Do the following:\n1. Read file\n2. Analyze content\n3. Report findings";

        // Act
        var (isDelegation, agentName, task) = TryParseDelegationSyntax(input);

        // Assert
        Assert.IsTrue(isDelegation);
        Assert.AreEqual("developer", agentName);
        Assert.IsTrue(task?.Contains("Do the following:"));
        Assert.IsTrue(task?.Contains("Report findings"));
    }

    [TestMethod]
    public void TryParseDelegationSyntax_NoAtSymbol_ReturnsFalse()
    {
        // Arrange
        var input = "Just a normal message";

        // Act
        var (isDelegation, agentName, task) = TryParseDelegationSyntax(input);

        // Assert
        Assert.IsFalse(isDelegation);
        Assert.IsNull(agentName);
        Assert.IsNull(task);
    }

    [TestMethod]
    public void TryParseDelegationSyntax_OnlyAtSymbol_ReturnsFalse()
    {
        // Arrange
        var input = "@developer";

        // Act
        var (isDelegation, agentName, task) = TryParseDelegationSyntax(input);

        // Assert
        Assert.IsFalse(isDelegation);
    }

    [TestMethod]
    public void TryParseDelegationSyntax_EmptyInput_ReturnsFalse()
    {
        // Arrange
        var input = "";

        // Act
        var (isDelegation, agentName, task) = TryParseDelegationSyntax(input);

        // Assert
        Assert.IsFalse(isDelegation);
    }

    [TestMethod]
    public void AppendAvailableSubAgentsList_WithAgents_AppendsCorrectly()
    {
        // Arrange
        var basePrompt = "You are an expert assistant.";
        var mainAgent = new AgentRef(
            AgentId: "expert",
            PromptTemplateVersion: "1.0",
            SystemPrompt: "You are an expert assistant.",
            ProviderId: "mock",
            ModelId: "mock",
            AllowedToolIds: null,
            SkillIds: null,
            SkillDiscoveryMode: null);

        var availableAgents = new[]
        {
            mainAgent,
            new AgentRef(
                AgentId: "developer",
                PromptTemplateVersion: "1.0",
                SystemPrompt: "You are a developer.\nUse tools to write code.",
                ProviderId: "mock",
                ModelId: "mock",
                AllowedToolIds: null,
                SkillIds: null,
                SkillDiscoveryMode: null),
            new AgentRef(
                AgentId: "code-reviewer",
                PromptTemplateVersion: "1.0",
                SystemPrompt: "You are a code reviewer.",
                ProviderId: "mock",
                ModelId: "mock",
                AllowedToolIds: null,
                SkillIds: null,
                SkillDiscoveryMode: null)
        };

        // Act
        var enhanced = AppendAvailableSubAgentsList(basePrompt, mainAgent, availableAgents);

        // Assert
        StringAssert.Contains(enhanced, "## Sub-Agent Delegation");
        StringAssert.Contains(enhanced, "mcli_delegate_agent");
        StringAssert.Contains(enhanced, "mcli_agent_job_create");
        StringAssert.Contains(enhanced, "mcli_agent_job_wait");
        StringAssert.Contains(enhanced, "@developer");
        StringAssert.Contains(enhanced, "@code-reviewer");
        StringAssert.Contains(enhanced, "You are a developer.");
        StringAssert.Contains(enhanced, "result");
        Assert.DoesNotContain("**@expert**", enhanced); // 主Agent不应该出现在列表中
    }

    [TestMethod]
    public void AppendAvailableSubAgentsList_OnlyOneAgent_NoAppend()
    {
        // Arrange
        var basePrompt = "You are an expert assistant.";
        var mainAgent = new AgentRef(
            AgentId: "expert",
            PromptTemplateVersion: "1.0",
            SystemPrompt: "You are an expert assistant.",
            ProviderId: "mock",
            ModelId: "mock",
            AllowedToolIds: null,
            SkillIds: null,
            SkillDiscoveryMode: null);

        var availableAgents = new[] { mainAgent };

        // Act
        var enhanced = AppendAvailableSubAgentsList(basePrompt, mainAgent, availableAgents);

        // Assert
        Assert.AreEqual(basePrompt, enhanced); // 应该完全相同
    }

    [TestMethod]
    public void AppendAvailableSubAgentsList_NullAgents_NoAppend()
    {
        // Arrange
        var basePrompt = "You are an expert assistant.";
        var mainAgent = new AgentRef(
            AgentId: "expert",
            PromptTemplateVersion: "1.0",
            SystemPrompt: "You are an expert assistant.",
            ProviderId: "mock",
            ModelId: "mock",
            AllowedToolIds: null,
            SkillIds: null,
            SkillDiscoveryMode: null);

        // Act
        var enhanced = AppendAvailableSubAgentsList(basePrompt, mainAgent, null);

        // Assert
        Assert.AreEqual(basePrompt, enhanced);
    }

    // 复制ExpertModeOrchestrator中的私有方法用于测试
    private static (bool isDelegation, string? agentName, string? task) TryParseDelegationSyntax(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return (false, null, null);
        }

        var trimmedInput = input.Trim();
        if (!trimmedInput.StartsWith('@'))
        {
            return (false, null, null);
        }

        var match = Regex.Match(
            trimmedInput,
            @"^@([\w\-]+)\s+(.+)$",
            RegexOptions.Singleline);

        if (match.Success)
        {
            var agentName = match.Groups[1].Value;
            var task = match.Groups[2].Value;
            return (true, agentName, task);
        }
        return (false, null, null);
    }

    private static string AppendAvailableSubAgentsList(
        string baseInstructions,
        AgentRef primaryAgent,
        AgentRef[]? availableAgents)
    {
        if (availableAgents is not { Length: > 1 })
        {
            return baseInstructions;
        }

        var agentListBuilder = new System.Text.StringBuilder();
        agentListBuilder.AppendLine();
        agentListBuilder.AppendLine("## Sub-Agent Delegation in Expert Mode");
        agentListBuilder.AppendLine();
        agentListBuilder.AppendLine("### Synchronous single-agent handoff");
        agentListBuilder.AppendLine(
            "Use `mcli_delegate_agent` when you need one specialist and must block for its `result` immediately.");
        agentListBuilder.AppendLine();
        agentListBuilder.AppendLine("### Parallel background jobs (no scheduling)");
        agentListBuilder.AppendLine(
            "Use these tools to run multiple specialists in parallel without blocking the create call:");
        agentListBuilder.AppendLine("- `mcli_agent_job_create` / `mcli_agent_job_create_many`: enqueue jobs, get `jobId`(s)");
        agentListBuilder.AppendLine("- `mcli_agent_job_status`: query state");
        agentListBuilder.AppendLine(
            "- `mcli_agent_job_result`: recover finished result(s) (`jobId` or `jobIds`); non-terminal returns ready=false");
        agentListBuilder.AppendLine(
            "- `mcli_agent_job_wait`: wait until terminal (or timeout) then recover results — preferred after create_many");
        agentListBuilder.AppendLine("- `mcli_agent_job_cancel`: request cancel");
        agentListBuilder.AppendLine(
            "There is no cron/timer API; long multi-step orchestration belongs in Work mode.");
        agentListBuilder.AppendLine();
        agentListBuilder.AppendLine(
            "Users may also start a turn with `@agentName <task>`; that path is handled before your turn.");
        agentListBuilder.AppendLine();
        agentListBuilder.AppendLine("Available sub-agents:");
        agentListBuilder.AppendLine();

        foreach (var agent in availableAgents)
        {
            if (!string.Equals(agent.AgentId, primaryAgent.AgentId, StringComparison.Ordinal))
            {
                var description = agent.SystemPrompt?.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault() ?? string.Empty;
                agentListBuilder.AppendLine(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"- **{agent.AgentId}** (`@{agent.AgentId}`) - {description}");
            }
        }
        return baseInstructions + agentListBuilder.ToString();
    }
}
