using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Core.Tests;

[TestClass]
public sealed class ToolCatalogFilteringDebugTests
{
    [TestMethod]
    public void TestToolWithDefaultSupportedModes_ShouldBeVisibleInExpertMode()
    {
        // 模拟测试工具的创建方式（只传6个参数）
        var testTool = new ToolDescriptor(
            ToolId: "mcli_fs_calculate",
            Namespace: "fs",
            DisplayName: "Calculate",
            Description: "Perform calculation",
            InputSchemaJson: "{}",
            OutputSchemaJson: "{}");

        var catalog = new ToolCatalogSnapshot("v1", "v1", new[] { testTool });
        var filtered = catalog.FilterByMode("expert");

        // 验证工具在过滤后仍然可见
        Assert.IsNotNull(filtered);
        Assert.HasCount(1, filtered.Tools, "Expected 1 tool in filtered catalog");

        var toolIds = filtered.Tools.Select(t => t.ToolId).ToList();
        CollectionAssert.Contains(toolIds, "mcli_fs_calculate", "Test tool should be visible in Expert mode");

        // 验证 SupportedModes 是 null
        var retrievedTool = filtered.Tools[0];
        Assert.IsNull(retrievedTool.SupportedModes, "SupportedModes should be null for test tool");
    }
}
