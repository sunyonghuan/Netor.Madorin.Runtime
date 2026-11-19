using Madorin.AI.Runtime.Services.Memory;
using Madorin.AI.Runtime.Services.Prompt;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

/// <summary>验证工作区身份注入与记忆工作区标识计算。</summary>
[TestClass]
public sealed class WorkspaceEnvironmentPromptTests
{
    private const string ProjectId = "01M6RJCSMGWFP2TFZM2KMQ967G";
    private const string ExpectedWorkspaceId = "56906cf240965c29707b5dd0af52a96e";

    [TestMethod]
    public void Compute_UsesProjectIdMd5()
    {
        Assert.AreEqual(ExpectedWorkspaceId, MemoryWorkspaceId.Compute(ProjectId));
    }

    [TestMethod]
    public void Append_IncludesProjectAndWorkspaceIdentity()
    {
        var text = ConversationEnvironmentPrompt.Append(
            "system",
            @"E:\Workspace\多媒体处理",
            "zh-CN",
            new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.FromHours(8)),
            ProjectId,
            ExpectedWorkspaceId);

        StringAssert.Contains(text, "workspace: E:\\Workspace\\多媒体处理");
        StringAssert.Contains(text, "project_id: " + ProjectId);
        StringAssert.Contains(text, "workspace_id: " + ExpectedWorkspaceId);
    }

    [TestMethod]
    public void Append_OmitsIdentityWhenMissing()
    {
        var text = ConversationEnvironmentPrompt.Append(
            "system",
            @"E:\Workspace\多媒体处理",
            "zh-CN");

        Assert.IsFalse(text.Contains("project_id:", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("workspace_id:", StringComparison.Ordinal));
    }
}
