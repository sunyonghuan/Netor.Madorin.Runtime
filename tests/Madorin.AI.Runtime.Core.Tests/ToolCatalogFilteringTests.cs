using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Core.Tests;

[TestClass]
public sealed class ToolCatalogFilteringTests
{
    [TestMethod]
    public void ExpertMode_ShouldNotSeeWorkAndMeetingTools()
    {
        var allTools = new[]
        {
            CreateExpertPlanTool(),       // SupportedModes: ["expert"]
            CreateWorkSetPlanTool(),      // SupportedModes: ["work"]
            CreateMeetingConfirmEndTool(), // SupportedModes: ["meeting"]
            CreateFileReadTool()          // SupportedModes: null
        };

        var catalog = new ToolCatalogSnapshot("v1", "v1", allTools);
        var filtered = catalog.FilterByMode("expert");

        // Expert should only see expert tools and universal tools
        var toolIds = filtered.Tools.Select(t => t.ToolId).ToList();
        Assert.HasCount(2, toolIds);
        CollectionAssert.Contains(toolIds, "mcli_expert_create_plan");
        CollectionAssert.Contains(toolIds, "mcli_fs_read");
        CollectionAssert.DoesNotContain(toolIds, "mcli_work_set_plan");
        CollectionAssert.DoesNotContain(toolIds, "mcli_meeting_confirm_end");
    }

    [TestMethod]
    public void WorkMode_ProjectLead_ShouldNotSeeAskUser()
    {
        var allTools = new[]
        {
            CreateAskUserTool(),          // SupportedModes: ["work", "meeting"]
            CreateWorkDispatchStepTool(), // SupportedModes: ["work"]
            CreateFileReadTool()          // SupportedModes: null
        };

        var catalog = new ToolCatalogSnapshot("v1", "v1", allTools);
        var filtered = catalog.FilterByMode("work", "project-lead");

        // project-lead cannot use ask_user
        var toolIds = filtered.Tools.Select(t => t.ToolId).ToArray();
        CollectionAssert.DoesNotContain(toolIds, "mcli_ask_user");
        CollectionAssert.Contains(toolIds, "mcli_work_dispatch_step");
        CollectionAssert.Contains(toolIds, "mcli_fs_read");
    }

    [TestMethod]
    public void WorkMode_GeneralManager_ShouldSeeAllWorkTools()
    {
        var allTools = new[]
        {
            CreateAskUserTool(),          // SupportedModes: ["work", "meeting"]
            CreateWorkSetPlanTool(),      // SupportedModes: ["work"]
            CreateWorkDispatchStepTool(), // SupportedModes: ["work"]
            CreateFileReadTool()          // SupportedModes: null
        };

        var catalog = new ToolCatalogSnapshot("v1", "v1", allTools);
        var filtered = catalog.FilterByMode("work", "general-manager");

        // general-manager can use all work tools
        var toolIds = filtered.Tools.Select(t => t.ToolId).ToArray();
        CollectionAssert.Contains(toolIds, "mcli_ask_user");
        CollectionAssert.Contains(toolIds, "mcli_work_set_plan");
        CollectionAssert.Contains(toolIds, "mcli_work_dispatch_step");
        CollectionAssert.Contains(toolIds, "mcli_fs_read");
    }

    [TestMethod]
    public void MeetingMode_Participant_ShouldNotSeeHostTools()
    {
        var allTools = new[]
        {
            CreateMeetingOutputSummaryTool(), // SupportedModes: ["meeting"]
            CreateMeetingConfirmEndTool(),    // SupportedModes: ["meeting"]
            CreateMeetingListAttachmentsTool(), // SupportedModes: ["meeting"]
            CreateAskUserTool(),              // SupportedModes: ["work", "meeting"]
            CreateFileReadTool()              // SupportedModes: null
        };

        var catalog = new ToolCatalogSnapshot("v1", "v1", allTools);
        var filtered = catalog.FilterByMode("meeting", "participant");

        // participant cannot use host-only tools
        var toolIds = filtered.Tools.Select(t => t.ToolId).ToArray();
        CollectionAssert.DoesNotContain(toolIds, "mcli_meeting_output_summary");
        CollectionAssert.DoesNotContain(toolIds, "mcli_meeting_confirm_end");
        CollectionAssert.DoesNotContain(toolIds, "mcli_ask_user");
        CollectionAssert.Contains(toolIds, "mcli_meeting_list_attachments");
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
        Assert.HasCount(1, catalog.FilterByMode("work").Tools);
        Assert.HasCount(1, catalog.FilterByMode("meeting").Tools);
    }

    [TestMethod]
    public void FilterByMode_WithNullMode_ShouldReturnAllTools()
    {
        var allTools = new[]
        {
            CreateExpertPlanTool(),
            CreateWorkSetPlanTool(),
            CreateFileReadTool()
        };

        var catalog = new ToolCatalogSnapshot("v1", "v1", allTools);
        var filtered = catalog.FilterByMode("expert");

        // Only expert and universal tools visible
        Assert.HasCount(2, filtered.Tools);
    }

    // Helper methods to create test tools
    private static readonly string[] ExpertModes = ["expert"];
    private static readonly string[] WorkModes = ["work"];
    private static readonly string[] MeetingModes = ["meeting"];
    private static readonly string[] WorkMeetingModes = ["work", "meeting"];

    private static ToolDescriptor CreateExpertPlanTool() => new(
        ToolId: "mcli_expert_create_plan",
        Namespace: "expert",
        DisplayName: "Create Execution Plan",
        Description: "Create a step-by-step execution plan for medium-complexity tasks.",
        InputSchemaJson: "{}",
        SupportedModes: ExpertModes);

    private static ToolDescriptor CreateWorkSetPlanTool() => new(
        ToolId: "mcli_work_set_plan",
        Namespace: "work",
        DisplayName: "Set Work Plan",
        Description: "Set the work plan for task execution.",
        InputSchemaJson: "{}",
        SupportedModes: WorkModes);

    private static ToolDescriptor CreateWorkDispatchStepTool() => new(
        ToolId: "mcli_work_dispatch_step",
        Namespace: "work",
        DisplayName: "Dispatch Step",
        Description: "Dispatch a step to an agent.",
        InputSchemaJson: "{}",
        SupportedModes: WorkModes);

    private static ToolDescriptor CreateMeetingOutputSummaryTool() => new(
        ToolId: "mcli_meeting_output_summary",
        Namespace: "meeting",
        DisplayName: "Output Meeting Summary",
        Description: "Output the meeting summary.",
        InputSchemaJson: "{}",
        SupportedModes: MeetingModes);

    private static ToolDescriptor CreateMeetingConfirmEndTool() => new(
        ToolId: "mcli_meeting_confirm_end",
        Namespace: "meeting",
        DisplayName: "Confirm End",
        Description: "Confirm the end of meeting.",
        InputSchemaJson: "{}",
        SupportedModes: MeetingModes);

    private static ToolDescriptor CreateMeetingListAttachmentsTool() => new(
        ToolId: "mcli_meeting_list_attachments",
        Namespace: "meeting",
        DisplayName: "List Attachments",
        Description: "List meeting attachments.",
        InputSchemaJson: "{}",
        SupportedModes: MeetingModes);

    private static ToolDescriptor CreateAskUserTool() => new(
        ToolId: "mcli_ask_user",
        Namespace: "interaction",
        DisplayName: "Ask User",
        Description: "Ask the user a question.",
        InputSchemaJson: "{}",
        SupportedModes: WorkMeetingModes);

    private static ToolDescriptor CreateFileReadTool() => new(
        ToolId: "mcli_fs_read",
        Namespace: "filesystem",
        DisplayName: "Read File",
        Description: "Read file contents.",
        InputSchemaJson: "{}",
        SupportedModes: null);  // Universal tool, visible in all modes
}

