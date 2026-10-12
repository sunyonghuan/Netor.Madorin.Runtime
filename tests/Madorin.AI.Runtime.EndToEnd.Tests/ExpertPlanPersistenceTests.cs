using Madorin.AI.Runtime.Contracts.Expert;
using Madorin.AI.Runtime.Modes.Expert;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class ExpertPlanPersistenceTests
{
    private string _workspaceRoot = null!;

    [TestInitialize]
    public void Initialize()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), $"expert-plan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspaceRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_workspaceRoot))
        {
            Directory.Delete(_workspaceRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task RestoreActivePlan_DoesNotReturnAnotherSessionsPlan()
    {
        var first = new ExpertPlanManager(_workspaceRoot);
        var second = new ExpertPlanManager(_workspaceRoot);
        var planA = await first.CreatePlanAsync("session-a", "run-a", CreateRequest("A"));
        var planB = await second.CreatePlanAsync("session-b", "run-b", CreateRequest("B"));

        var restoredA = await new ExpertPlanManager(_workspaceRoot)
            .RestoreActivePlanAsync("session-a");
        var restoredB = await new ExpertPlanManager(_workspaceRoot)
            .RestoreActivePlanAsync("session-b");

        Assert.AreEqual(planA.PlanId, restoredA?.PlanId);
        Assert.AreEqual(planB.PlanId, restoredB?.PlanId);
        Assert.AreNotEqual(planA.PlanId, planB.PlanId);
    }

    [TestMethod]
    public async Task RestoreActivePlan_AfterCancellingLatestPlan_DoesNotResurrectOlderPlan()
    {
        var manager = new ExpertPlanManager(_workspaceRoot);
        await manager.CreatePlanAsync("session-a", "run-a", CreateRequest("old"));
        await manager.CreatePlanAsync("session-a", "run-b", CreateRequest("new"));
        await manager.AbandonPlanAsync();

        Assert.IsNull(await new ExpertPlanManager(_workspaceRoot)
            .RestoreActivePlanAsync("session-a"));
    }

    [TestMethod]
    public async Task RestoreActivePlan_PreservesUpdatedSteps()
    {
        var manager = new ExpertPlanManager(_workspaceRoot);
        var created = await manager.CreatePlanAsync("session-a", "run-a", CreateRequest("build"));
        await manager.UpdateStepStatusAsync("step-1", "run-a", ExpertStepStatus.Completed, 100, "done");

        var restored = await new ExpertPlanManager(_workspaceRoot)
            .RestoreActivePlanAsync("session-a");

        Assert.IsNotNull(restored);
        Assert.AreEqual(created.PlanId, restored.PlanId);
        Assert.AreEqual(ExpertStepStatus.Completed, restored.Steps[0].Status);
        Assert.AreEqual("done", restored.Steps[0].Result);
    }

    [TestMethod]
    public async Task RestoreActivePlan_LegacySharedPointer_DoesNotCrossSessions()
    {
        var store = new ExpertPlanFileStore(_workspaceRoot);
        var plan = new ExpertPlan(
            "plan-legacy", "session-a", "legacy", ExpertPlanStatus.Running,
            [new("step-1", "First", null, null, ExpertStepStatus.Running, 50, null, null, null, null)],
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var pointer = Path.Combine(_workspaceRoot, ".madorin", "plans", "active.json");
        await File.WriteAllTextAsync(pointer,
            System.Text.Json.JsonSerializer.Serialize(plan,
                Madorin.AI.Runtime.Contracts.Serialization.RuntimeJsonContext.Default.ExpertPlan));

        Assert.IsNull(await store.LoadActiveAsync("session-b"));
        Assert.AreEqual(plan.PlanId, (await store.LoadActiveAsync("session-a"))?.PlanId);
    }

    [TestMethod]
    public async Task RestoreActivePlan_WhenPointerIsCorrupt_FallsBackToSessionArchive()
    {
        var manager = new ExpertPlanManager(_workspaceRoot);
        var plan = await manager.CreatePlanAsync("session-a", "run-a", CreateRequest("recover"));
        var sessionDirectory = Assert.ContainsSingle(Directory.GetDirectories(
            Path.Combine(_workspaceRoot, ".madorin", "plans")));
        await File.WriteAllTextAsync(Path.Combine(sessionDirectory, "active.json"), "{broken");

        var restored = await new ExpertPlanManager(_workspaceRoot)
            .RestoreActivePlanAsync("session-a");

        Assert.AreEqual(plan.PlanId, restored?.PlanId);
        Assert.IsNull(await new ExpertPlanManager(_workspaceRoot)
            .RestoreActivePlanAsync("session-b"));
    }

    [TestMethod]
    public async Task RestoreActivePlan_WhenPointerIsCorrupt_DoesNotResurrectOlderPlan()
    {
        var store = new ExpertPlanFileStore(_workspaceRoot);
        var manager = new ExpertPlanManager(_workspaceRoot);
        var oldPlan = await manager.CreatePlanAsync("session-a", "run-a", CreateRequest("old"));
        await store.SaveAsync(oldPlan with
        {
            CreatedAt = oldPlan.CreatedAt.AddMinutes(-2),
            UpdatedAt = oldPlan.UpdatedAt.AddMinutes(5)
        });
        var newPlan = await manager.CreatePlanAsync("session-a", "run-b", CreateRequest("new"));
        await manager.AbandonPlanAsync();
        var sessionDirectory = Assert.ContainsSingle(Directory.GetDirectories(
            Path.Combine(_workspaceRoot, ".madorin", "plans")));
        await File.WriteAllTextAsync(Path.Combine(sessionDirectory, "active.json"), "{broken");

        Assert.IsNull(await new ExpertPlanManager(_workspaceRoot)
            .RestoreActivePlanAsync("session-a"));
        Assert.AreNotEqual(oldPlan.PlanId, newPlan.PlanId);
    }

    [TestMethod]
    public async Task RestoreActivePlan_WhenPointerIsStale_PrefersNewerArchive()
    {
        var store = new ExpertPlanFileStore(_workspaceRoot);
        var manager = new ExpertPlanManager(_workspaceRoot);
        var oldPlan = await manager.CreatePlanAsync("session-a", "run-a", CreateRequest("old"));
        var oldSnapshot = oldPlan with
        {
            CreatedAt = oldPlan.CreatedAt.AddMinutes(-2),
            UpdatedAt = oldPlan.UpdatedAt.AddMinutes(5)
        };
        await store.SaveAsync(oldSnapshot);
        var newPlan = await manager.CreatePlanAsync("session-a", "run-b", CreateRequest("new"));
        var sessionDirectory = Assert.ContainsSingle(Directory.GetDirectories(
            Path.Combine(_workspaceRoot, ".madorin", "plans")));
        await File.WriteAllTextAsync(
            Path.Combine(sessionDirectory, "active.json"),
            System.Text.Json.JsonSerializer.Serialize(oldSnapshot,
                Madorin.AI.Runtime.Contracts.Serialization.RuntimeJsonContext.Default.ExpertPlan));

        var restored = await new ExpertPlanManager(_workspaceRoot)
            .RestoreActivePlanAsync("session-a");

        Assert.AreEqual(newPlan.PlanId, restored?.PlanId);
    }

    [TestMethod]
    public async Task RestoreActivePlan_PreservesEvidenceAndRejectsTerminalReexecution()
    {
        var manager = new ExpertPlanManager(_workspaceRoot);
        await manager.CreatePlanAsync("session-a", "run-a", CreateRequest("evidence"));
        await manager.UpdateStepStatusAsync("step-1", "run-a", ExpertStepStatus.Running);
        var evidence = new ExpertToolEvidence("planner", "write-call", "mcli_fs_write",
            true, DateTimeOffset.UtcNow);
        await manager.RecordToolEvidenceAsync([evidence], TestContext.CancellationToken);
        var restored = new ExpertPlanManager(_workspaceRoot);
        var plan = await restored.RestoreActivePlanAsync("session-a");
        Assert.AreEqual("write-call", Assert.ContainsSingle(plan!.Steps[0].Evidence!).CallId);
        await restored.UpdateStepStatusWithEvidenceAsync("step-1", "run-b", ExpertStepStatus.Completed,
            100, "done", [evidence], TestContext.CancellationToken);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            restored.UpdateStepStatusAsync("step-1", "run-b", ExpertStepStatus.Running));
        Assert.AreEqual(ExpertStepStatus.Completed, restored.ActivePlan!.Steps[0].Status);
        Assert.AreEqual("done", restored.ActivePlan.Steps[0].Result);
    }

    [TestMethod]
    public async Task RestoreActivePlan_FailedEvidenceCannotBeOverwritten()
    {
        var manager = new ExpertPlanManager(_workspaceRoot);
        await manager.CreatePlanAsync("session-a", "run-a", CreateRequest("failed"));
        await manager.UpdateStepStatusAsync("step-1", "run-a", ExpertStepStatus.Failed,
            notes: "permission denied");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            manager.UpdateStepStatusAsync("step-1", "run-b", ExpertStepStatus.Completed, 100, "fake"));
        Assert.AreEqual("permission denied", manager.ActivePlan!.Steps[0].ErrorMessage);
    }

    [TestMethod]
    public async Task Plan_CompletesWhenRemainingStepsAreSkipped()
    {
        var manager = new ExpertPlanManager(_workspaceRoot);
        await manager.CreatePlanAsync("session-a", "run-a", CreateRequest("skip"));
        await manager.UpdateStepStatusAsync("step-1", "run-a", ExpertStepStatus.Completed, 100, "done");
        var plan = await manager.UpdateStepStatusAsync("step-2", "run-a", ExpertStepStatus.Skipped, notes: "not needed");

        Assert.AreEqual(ExpertPlanStatus.Completed, plan.Status);
    }

    public TestContext TestContext { get; set; } = null!;

    private static CreateExpertPlanRequest CreateRequest(string goal) => new(
        goal,
        [new("step-1", "First"), new("step-2", "Second")]);
}
