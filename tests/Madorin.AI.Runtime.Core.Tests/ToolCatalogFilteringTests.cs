using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Core.Tests;

[TestClass]
public sealed class ToolCatalogFilteringTests
{
    [TestMethod]
    public void ExpertMode_ShouldSeeExpertAndUniversalTools()
    {
        var allTools = new[]
        {
            CreateExpertPlanTool(),       // SupportedModes: ["expert"]
            CreateFileReadTool()          // SupportedModes: null (universal)
        };

        var catalog = new ToolCatalogSnapshot("v1", "v1", allTools);
        var filtered = catalog.FilterByMode("expert");

        // Expert should see expert tools and universal tools
        var toolIds = filtered.Tools.Select(t => t.ToolId).ToList();
        Assert.HasCount(2, toolIds);
        CollectionAssert.Contains(toolIds, "mcli_expert_create_plan");
        CollectionAssert.Contains(toolIds, "mcli_fs_read");
    }

    [TestMethod]
    public void HostTools_ShouldBeVisibleInAllModes()
    {
        var hostTool = new ToolDescriptor(
            ToolId: "host_custom_tool",
            Namespace: "host",
            DisplayName: "Custom Tool",
            Description: "Host provided custom tool",
            InputSchemaJson: "{}",
            SupportedModes: null  // Host tools default to null
        );

        var catalog = new ToolCatalogSnapshot("v1", "v1", new[] { hostTool });

        Assert.HasCount(1, catalog.FilterByMode("expert").Tools);
    }

    [TestMethod]
    public void FilterByMode_WithExpert_ShouldReturnExpertAndUniversal()
    {
        var allTools = new[]
        {
            CreateExpertPlanTool(),
            CreateFileReadTool()
        };

        var catalog = new ToolCatalogSnapshot("v1", "v1", allTools);
        var filtered = catalog.FilterByMode("expert");

        // Only expert and universal tools visible
        Assert.HasCount(2, filtered.Tools);
    }

    // Helper methods to create test tools
    private static readonly string[] ExpertModes = ["expert"];

    private static ToolDescriptor CreateExpertPlanTool() => new(
        ToolId: "mcli_expert_create_plan",
        Namespace: "expert",
        DisplayName: "Create Execution Plan",
        Description: "Create a step-by-step execution plan for medium-complexity tasks.",
        InputSchemaJson: "{}",
        SupportedModes: ExpertModes);

    private static ToolDescriptor CreateFileReadTool() => new(
        ToolId: "mcli_fs_read",
        Namespace: "filesystem",
        DisplayName: "Read File",
        Description: "Read file contents.",
        InputSchemaJson: "{}",
        SupportedModes: null);  // Universal tool, visible in all modes
}
