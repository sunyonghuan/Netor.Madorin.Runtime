using System.Text.Json;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Tools.Builtin;
using Madorin.AI.Runtime.Tools.Builtin.Work;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class WorkSetPlanToolTests
{
    private static readonly string[] WorkModes = ["work"];

    [TestMethod]
    public void Registry_SetPlan_IsWorkOnlyAndDoesNotRequireApproval()
    {
        var tool = new BuiltinToolRegistry().GetTools()
            .Single(static item => item.ToolId == BuiltinToolRegistry.WorkSetPlanToolId);

        Assert.IsTrue(tool.SupportedModes!.SequenceEqual(WorkModes));
        Assert.IsFalse(tool.RequiresApproval);
        Assert.IsTrue(new BuiltinWorkSetPlanToolExecutor().CanExecute(tool.ToolId));
    }

    [TestMethod]
    public async Task SetPlan_NativeDraft_ReturnsPlanWithoutPersisting()
    {
        var result = await ExecuteAsync("""
            {
              "planVersion": "1",
              "goal": "Ship the notes",
              "steps": [
                { "stepId": "s1", "goal": "Write notes", "targetAgentId": "worker" }
              ]
            }
            """);

        Assert.IsTrue(result.Success);
        using var output = JsonDocument.Parse(result.OutputJson!);
        Assert.IsTrue(output.RootElement.GetProperty("ok").GetBoolean());
        Assert.AreEqual(1, output.RootElement.GetProperty("stepCount").GetInt32());
        var plan = JsonSerializer.Deserialize(
            output.RootElement.GetProperty("plan").GetRawText(),
            RuntimeJsonContext.Default.WorkPlanDraft);
        Assert.IsNotNull(plan);
        Assert.AreEqual("Ship the notes", plan.Goal);
        Assert.AreEqual("worker", plan.Steps[0].TargetAgentId);
    }

    [TestMethod]
    public async Task SetPlan_MainSteps_FlattensSubStepsWithAcceptance()
    {
        var result = await ExecuteAsync("""
            {
              "goal": "Prepare release",
              "main_steps": [
                {
                  "title": "Draft",
                  "sub_steps": [
                    { "title": "Write notes", "acceptance_criteria": "Notes file exists" }
                  ]
                }
              ]
            }
            """);

        Assert.IsTrue(result.Success);
        using var output = JsonDocument.Parse(result.OutputJson!);
        Assert.AreEqual(1, output.RootElement.GetProperty("stepCount").GetInt32());
        var plan = JsonSerializer.Deserialize(
            output.RootElement.GetProperty("plan").GetRawText(),
            RuntimeJsonContext.Default.WorkPlanDraft);
        Assert.IsNotNull(plan);
        Assert.AreEqual("m1", plan.Steps[0].StepId);
        StringAssert.Contains(plan.Steps[0].Goal, "Notes file exists");
    }

    [TestMethod]
    public async Task SetPlan_SameAgentGates_FoldIntoOneDispatchStep()
    {
        var gates = string.Join(
            ",",
            Enumerable.Range(1, 10).Select(index =>
                "{\"title\":\"Gate " + index + "\",\"acceptance_criteria\":\"Gate " + index + " passed\",\"targetAgentId\":\"worker\"}"));
        var result = await ExecuteAsync(
            """
            {
              "goal": "Create ten documents",
              "main_steps": [
                { "title": "Document 01", "sub_steps": [
            """ + gates + "] } ] }");

        Assert.IsTrue(result.Success, result.Error);
        using var output = JsonDocument.Parse(result.OutputJson!);
        Assert.AreEqual(1, output.RootElement.GetProperty("stepCount").GetInt32());
        var plan = JsonSerializer.Deserialize(
            output.RootElement.GetProperty("plan").GetRawText(),
            RuntimeJsonContext.Default.WorkPlanDraft);
        Assert.IsNotNull(plan);
        Assert.AreEqual("m1", plan.Steps[0].StepId);
        StringAssert.Contains(plan.Steps[0].Goal, "Gate 1");
        StringAssert.Contains(plan.Steps[0].Goal, "Gate 10");
    }

    [TestMethod]
    public async Task SetPlan_OverMaxSteps_AsksForCollapsedPlan()
    {
        var result = await new BuiltinWorkSetPlanToolExecutor(maxStepsPerRun: 2).ExecuteAsync(
            new ToolInvocation(
                "call-1",
                BuiltinToolRegistry.WorkSetPlanToolId,
                "gm",
                null,
                """
                {
                  "planVersion": "1",
                  "goal": "Too fine",
                  "steps": [
                    { "stepId": "s1", "goal": "A", "targetAgentId": "worker" },
                    { "stepId": "s2", "goal": "B", "targetAgentId": "worker" },
                    { "stepId": "s3", "goal": "C", "targetAgentId": "worker" }
                  ]
                }
                """,
                "run-1",
                "session-1"));

        AssertFailure(result, "too_many_steps");
        AssertFailure(result, "MaxStepsPerRun=2");
    }

    [TestMethod]
    public async Task SetPlan_PlaceholderOrMissingAcceptance_ReturnsOkFalse()
    {
        var placeholder = await ExecuteAsync("""
            { "goal": "Ship {{name}}", "planVersion": "1", "steps": [] }
            """);
        var missingAcceptance = await ExecuteAsync("""
            {
              "main_steps": [
                { "title": "Draft", "sub_steps": [ { "title": "Write notes" } ] }
              ]
            }
            """);

        AssertFailure(placeholder, "invalid_plan");
        AssertFailure(missingAcceptance, "acceptance_criteria");
    }

    private static async Task<ToolResult> ExecuteAsync(string arguments)
    {
        var executor = new BuiltinWorkSetPlanToolExecutor();
        return await executor.ExecuteAsync(new ToolInvocation(
            "call-1",
            BuiltinToolRegistry.WorkSetPlanToolId,
            "gm",
            null,
            arguments,
            "run-1",
            "session-1"));
    }

    private static void AssertFailure(ToolResult result, string expected)
    {
        Assert.IsFalse(result.Success);
        using var output = JsonDocument.Parse(result.OutputJson!);
        Assert.IsFalse(output.RootElement.GetProperty("ok").GetBoolean());
        var message = output.RootElement.GetProperty("message").GetString() ?? string.Empty;
        var error = output.RootElement.GetProperty("error").GetString() ?? string.Empty;
        StringAssert.Contains(message + error, expected);
    }
}
