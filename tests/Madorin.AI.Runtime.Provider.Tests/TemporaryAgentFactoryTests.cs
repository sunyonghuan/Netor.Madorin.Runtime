using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Modes.Expert;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class TemporaryAgentFactoryTests
{
    private static readonly string[] ParentTools = ["mcli_fs_read", "mcli_fs_write", "mcli_fs_delete", "mcli_process", "mcli_fs_search"];
    private static readonly string[] RequestedTools = ["mcli_fs_read", "mcli_fs_write", "mcli_fs_delete", "mcli_process", "mcli_fs_search"];
    private static readonly string[] ReadOnlyTools = ["mcli_fs_read", "mcli_fs_search", "mcli_fs_inspect", "mcli_fs_list"];
    private static readonly string[] ActualTools = ["mcli_fs_read", "mcli_fs_write", "mcli_fs_delete", "mcli_fs_search"];
    private static readonly string[] Capabilities = ["csharp.analysis"];

    [TestMethod]
    public void Create_InheritsProviderAndIntersectsParentAllowList()
    {
        var parent = new AgentRef(
            "planner", "1", "parent", "deepseek", "model",
            ParentTools,
            Capabilities: ["filesystem.read", "filesystem.write"]);

        var agent = TemporaryAgentFactory.Create(
            parent,
            "代码搜索助手",
            "查找 CalculatePrice 的调用点",
            ["csharp.analysis"],
            RequestedTools);

        StringAssert.StartsWith(agent.AgentId, "temp-");
        Assert.IsTrue(Guid.TryParseExact(agent.AgentId[5..], "N", out _));
        Assert.AreNotEqual(parent.AgentId, agent.AgentId);
        Assert.AreEqual("deepseek", agent.ProviderId);
        Assert.AreEqual("model", agent.ModelId);
        CollectionAssert.AreEquivalent(ActualTools, agent.AllowedToolIds);
        Assert.IsNotNull(agent.AllowedToolIds);
        Assert.Contains("mcli_fs_write", agent.AllowedToolIds);
        Assert.DoesNotContain("mcli_process", agent.AllowedToolIds);
        StringAssert.Contains(agent.SystemPrompt, "File writes and deletes are allowed only");
        CollectionAssert.AreEquivalent(Capabilities, agent.Capabilities);
    }

    [TestMethod]
    public void Create_RejectsMemoryMutationAndSiblingJobControl()
    {
        string[] requested = ["mcli_fs_read", "mcli_memory_write", "mcli_memory_append",
            "mcli_memory_update", "mcli_memory_delete", "mcli_agent_job_cancel"];
        var parent = new AgentRef("planner", "1", "parent", AllowedToolIds: requested);
        var child = TemporaryAgentFactory.Create(parent, "reviewer", "read", null, requested);
        string[] expected = ["mcli_fs_read"];
        CollectionAssert.AreEquivalent(expected, child.AllowedToolIds);
    }

    [TestMethod]
    public void Create_ExplicitEmptyListsNeverGrantTools()
    {
        var parent = new AgentRef("planner", "1", "parent", AllowedToolIds: []);
        var first = TemporaryAgentFactory.Create(parent, "writer", "write", null, RequestedTools);
        var second = TemporaryAgentFactory.Create(parent with { AllowedToolIds = null }, "writer", "write", null, []);
        Assert.IsEmpty(first.AllowedToolIds!);
        Assert.IsEmpty(second.AllowedToolIds!);
        Assert.AreNotEqual(first.AgentId, second.AgentId);
    }

    [TestMethod]
    public void Create_WithNoParentAllowListUsesReadOnlyDefaults()
    {
        var parent = new AgentRef("planner", "1", "parent");

        var agent = TemporaryAgentFactory.Create(
            parent,
            "搜索助手",
            "搜索项目代码",
            null,
            null);

        CollectionAssert.AreEquivalent(
            ReadOnlyTools,
            agent.AllowedToolIds);
    }
}
