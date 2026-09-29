using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
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

        Assert.IsFalse(result.Success);
        AssertFailure(result, "targetAgentId");
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
        Assert.AreEqual(11, output.RootElement.GetProperty("stepCount").GetInt32());
        var plan = JsonSerializer.Deserialize(
            output.RootElement.GetProperty("plan").GetRawText(),
            RuntimeJsonContext.Default.WorkPlanDraft);
        Assert.IsNotNull(plan);
        Assert.AreEqual("m1", plan.Steps[0].StepId);
        Assert.AreEqual("m1", plan.Steps[1].ParentStepId);
        Assert.AreEqual("worker", plan.Steps[1].TargetAgentId);
        Assert.AreEqual("m1-s10", plan.Steps[^1].StepId);
    }

    [TestMethod]
    public async Task SetPlan_NextPhase_DependsOnPreviousPhase()
    {
        var result = await ExecuteAsync("""
            {
              "goal": "Two phases",
              "main_steps": [
                { "title": "One", "sub_steps": [
                  { "title": "A", "acceptance_criteria": "A done", "targetAgentId": "worker" }
                ]},
                { "title": "Two", "sub_steps": [
                  { "title": "B", "acceptance_criteria": "B done", "targetAgentId": "worker" }
                ]}
              ]
            }
            """);

        Assert.IsTrue(result.Success, result.Error);
        using var output = JsonDocument.Parse(result.OutputJson!);
        var plan = JsonSerializer.Deserialize(
            output.RootElement.GetProperty("plan").GetRawText(),
            RuntimeJsonContext.Default.WorkPlanDraft);
        Assert.IsNotNull(plan);
        var second = plan.Steps.Single(step => step.StepId == "m2-s1");
        Assert.IsNotNull(second.DependsOn);
        Assert.HasCount(1, second.DependsOn);
        Assert.AreEqual("m1-s1", second.DependsOn[0]);
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

    [TestMethod]
    public void Registry_SetPlan_SchemaNamesTheStepContract()
    {
        var tool = new BuiltinToolRegistry().GetTools()
            .Single(static item => item.ToolId == BuiltinToolRegistry.WorkSetPlanToolId);

        using var schema = JsonDocument.Parse(tool.InputSchemaJson);
        var properties = schema.RootElement.GetProperty("properties");
        var stepProperties = properties.GetProperty("steps").GetProperty("items").GetProperty("properties");
        Assert.IsTrue(stepProperties.TryGetProperty("stepId", out _));
        Assert.IsTrue(stepProperties.TryGetProperty("targetAgentId", out _));
        Assert.IsTrue(stepProperties.TryGetProperty("parentStepId", out _));
        var subStepProperties = properties
            .GetProperty("main_steps")
            .GetProperty("items")
            .GetProperty("properties")
            .GetProperty("sub_steps")
            .GetProperty("items")
            .GetProperty("properties");
        Assert.IsTrue(subStepProperties.TryGetProperty("acceptance_criteria", out _));
        Assert.IsTrue(subStepProperties.TryGetProperty("targetAgentId", out _));
        StringAssert.Contains(tool.Description, "parentStepId");
        StringAssert.Contains(tool.Description, "sub_steps");
    }

    [TestMethod]
    public async Task SetPlan_NestedSubSteps_FlattensWithParentStepId()
    {
        var result = await ExecuteAsync(
            """
            {
              "planVersion": "1",
              "goal": "Delete the samples",
              "steps": [
                {
                  "stepId": "1",
                  "title": "删除",
                  "goal": "删除测试文档",
                  "subSteps": [
                    {
                      "title": "删除docx",
                      "goal": "删除文档02到10",
                      "targetAgentId": "engineer"
                    }
                  ]
                }
              ]
            }
            """);

        Assert.IsTrue(result.Success, result.Error);
        var plan = ReadPlan(result);
        Assert.HasCount(2, plan.Steps);
        Assert.AreEqual("1", plan.Steps[0].StepId);
        Assert.IsTrue(string.IsNullOrEmpty(plan.Steps[0].TargetAgentId));
        Assert.AreEqual("1-s1", plan.Steps[1].StepId);
        Assert.AreEqual("1", plan.Steps[1].ParentStepId);
        Assert.AreEqual("engineer", plan.Steps[1].TargetAgentId);
    }

    [TestMethod]
    public async Task SetPlan_AgentIdAlias_BecomesTargetAgentId()
    {
        var result = await ExecuteAsync(
            """
            {
              "planVersion": "1",
              "goal": "Ship the notes",
              "steps": [
                { "stepId": "s1", "goal": "Write notes", "agentId": "worker" }
              ]
            }
            """);

        Assert.IsTrue(result.Success, result.Error);
        var plan = ReadPlan(result);
        Assert.AreEqual("worker", plan.Steps[0].TargetAgentId);
    }

    [TestMethod]
    public async Task SetPlan_MainStepsCamelCaseAliases_AreAccepted()
    {
        var result = await ExecuteAsync(
            """
            {
              "goal": "Prepare release",
              "main_steps": [
                {
                  "title": "Draft",
                  "subSteps": [
                    {
                      "title": "Write notes",
                      "acceptanceCriteria": "Notes file exists",
                      "agentId": "worker"
                    }
                  ]
                }
              ]
            }
            """);

        Assert.IsTrue(result.Success, result.Error);
        var plan = ReadPlan(result);
        var leaf = plan.Steps.Single(step => step.ParentStepId == "m1");
        Assert.AreEqual("worker", leaf.TargetAgentId);
        StringAssert.Contains(leaf.Goal, "Notes file exists");
    }

    [TestMethod]
    public async Task SetPlan_LeafWithoutAgent_NamesTheRequiredField()
    {
        var result = await ExecuteAsync(
            """
            {
              "planVersion": "1",
              "goal": "Ship the notes",
              "steps": [
                { "stepId": "s1", "goal": "Write notes" }
              ]
            }
            """);

        AssertFailure(result, "targetAgentId is empty");
        AssertFailure(result, "parentStepId");
    }

    private static WorkPlanDraft ReadPlan(ToolResult result)
    {
        using var output = JsonDocument.Parse(result.OutputJson!);
        var plan = JsonSerializer.Deserialize(
            output.RootElement.GetProperty("plan").GetRawText(),
            RuntimeJsonContext.Default.WorkPlanDraft);
        Assert.IsNotNull(plan);
        return plan;
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
