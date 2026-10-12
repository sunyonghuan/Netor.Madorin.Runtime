using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Tools.Builtin;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class BuiltinExpertPlanToolExecutorTests
{
    [TestMethod]
    public async Task CreatePlan_UsesGoalAndAllowsOptionalAgentAssignment()
    {
        var result = await ExecuteAsync(
            BuiltinToolRegistry.ExpertCreatePlanToolId,
            """{"goal":"Write a README","steps":[{"title":"Draft","description":"Draft README","agentName":"writer"},{"title":"Review","description":"Review README","agentName":"reviewer"}]}""");

        Assert.IsTrue(result.Success, result.OutputJson);
        using var output = JsonDocument.Parse(result.OutputJson ?? "{}");
        Assert.AreEqual("Write a README", output.RootElement.GetProperty("goal").GetString());
        Assert.AreEqual(2, output.RootElement.GetProperty("totalSteps").GetInt32());
        Assert.AreEqual("pending-orchestrator", output.RootElement.GetProperty("planId").GetString());
    }

    [TestMethod]
    public async Task CreatePlan_RejectsTitleOnlyLegacyShape()
    {
        var result = await ExecuteAsync(
            BuiltinToolRegistry.ExpertCreatePlanToolId,
            """{"title":"Write a README","steps":[{"title":"Draft","description":"Draft README"},{"title":"Review","description":"Review README"}]}""");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.OutputJson, "goal");
    }

    [TestMethod]
    public async Task CreatePlan_RequiresTwoToFifteenSteps()
    {
        var result = await ExecuteAsync(
            BuiltinToolRegistry.ExpertCreatePlanToolId,
            """{"goal":"Write a README","steps":[{"title":"Draft","description":"Draft README"}]}""");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.OutputJson, "2-15");
    }

    private static Task<ToolResult> ExecuteAsync(string toolId, string arguments) =>
        new BuiltinExpertPlanToolExecutor().ExecuteAsync(new ToolInvocation(
            "call-1", toolId, "planner", null, arguments, "run-1", "session-1", "invocation-1"));
}
