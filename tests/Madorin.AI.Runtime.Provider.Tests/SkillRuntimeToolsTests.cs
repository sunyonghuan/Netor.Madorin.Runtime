using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Services.Skills;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Tools.Builtin;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class SkillRuntimeToolsTests
{
    [TestMethod]
    public void AdvertisedTools_AreVisibleOnProviderCatalog()
    {
        var snapshot = new ToolCatalogSnapshot("host", "effective", [])
            .WithInternalTools(SkillRuntimeTools.GatewayDescriptors);

        Assert.IsTrue(snapshot.TryGetTool(SkillScriptAuthorization.ToolId, out _));
        Assert.IsFalse(snapshot.Tools.Any(static tool =>
            string.Equals(tool.ToolId, SkillScriptAuthorization.ToolId, StringComparison.Ordinal)));
        Assert.IsTrue(snapshot.Tools.Any(static tool =>
            string.Equals(tool.ToolId, SkillRuntimeTools.ReadToolId, StringComparison.Ordinal)));
        Assert.IsTrue(snapshot.Tools.Any(static tool =>
            string.Equals(tool.DisplayName, "load_skill", StringComparison.Ordinal)));
        Assert.IsFalse(snapshot.Tools.Any(static tool =>
            string.Equals(tool.ToolId, SkillRuntimeTools.LoadToolId, StringComparison.Ordinal)));
        Assert.IsFalse(snapshot.Tools.Any(static tool =>
            string.Equals(tool.DisplayName, "read_skill_resource", StringComparison.Ordinal)));
        Assert.IsTrue(snapshot.Tools.Any(static tool =>
            string.Equals(tool.DisplayName, "run_skill_script", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ConsentResolver_AcceptsLoadSkill()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "madorin-skill-tools", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var resolver = new BuiltinToolConsentScopeResolver();
            var invocation = new ToolInvocation(
                "call-1",
                SkillRuntimeTools.LoadToolId,
                "agent-1",
                ParentAgentId: null,
                """{"name":"demo"}""",
                "run-1",
                "session-1",
                "inv-1");
            var resolution = resolver.Resolve(invocation, SkillRuntimeTools.Load, workspace);
            Assert.IsTrue(resolution.IsSuccess, resolution.Error);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [TestMethod]
    public void ConsentResolver_AcceptsUnifiedSkillRead()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "madorin-skill-tools", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var resolver = new BuiltinToolConsentScopeResolver();
            var load = resolver.Resolve(
                new ToolInvocation(
                    "call-1",
                    SkillRuntimeTools.ReadToolId,
                    "agent-1",
                    ParentAgentId: null,
                    """{"name":"demo"}""",
                    "run-1",
                    "session-1",
                    "inv-1"),
                SkillRuntimeTools.Read,
                workspace);
            var resource = resolver.Resolve(
                new ToolInvocation(
                    "call-2",
                    SkillRuntimeTools.ReadToolId,
                    "agent-1",
                    ParentAgentId: null,
                    """{"name":"demo","resource":"references/guide.md"}""",
                    "run-1",
                    "session-1",
                    "inv-1"),
                SkillRuntimeTools.Read,
                workspace);

            Assert.IsTrue(load.IsSuccess, load.Error);
            Assert.AreEqual("load skill", load.Scope!.DetailSummary);
            Assert.IsTrue(resource.IsSuccess, resource.Error);
            Assert.AreEqual("read skill resource", resource.Scope!.DetailSummary);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
