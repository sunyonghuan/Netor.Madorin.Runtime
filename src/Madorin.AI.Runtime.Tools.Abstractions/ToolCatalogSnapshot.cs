using System.Collections.Frozen;

namespace Madorin.AI.Runtime.Tools.Abstractions;

/// <summary>An immutable merged view of built-in and host-owned tools.</summary>
public sealed class ToolCatalogSnapshot
{
    private readonly FrozenDictionary<string, ToolDescriptor> _tools;
    private readonly IReadOnlyList<ToolDescriptor> _allTools;
    private readonly IReadOnlyList<ToolDescriptor> _orderedTools;

    public ToolCatalogSnapshot(
        string hostCatalogVersion,
        string effectiveVersion,
        IEnumerable<ToolDescriptor> tools)
    {
        HostCatalogVersion = hostCatalogVersion;
        EffectiveVersion = effectiveVersion;
        var copies = tools
            .Select(CloneDescriptor)
            .OrderBy(static tool => tool.ToolId, StringComparer.Ordinal)
            .ToArray();
        _tools = copies.ToFrozenDictionary(static tool => tool.ToolId, StringComparer.Ordinal);
        _allTools = Array.AsReadOnly(copies);
        _orderedTools = Array.AsReadOnly(copies
            .Where(static tool => tool.Tags?.Contains("internal", StringComparer.Ordinal) != true)
            .ToArray());
    }

    /// <summary>Gets the version assigned by the host to its catalog partition.</summary>
    public string HostCatalogVersion { get; }

    /// <summary>Gets the SHA-256 version of the normalized merged catalog.</summary>
    public string EffectiveVersion { get; }

    /// <summary>Gets all tools in ordinal tool-id order.</summary>
    public IReadOnlyList<ToolDescriptor> Tools => _orderedTools;

    /// <summary>Gets all descriptors, including Runtime-internal descriptors used only by the Gateway.</summary>
    public IReadOnlyList<ToolDescriptor> AllTools => _allTools;

    /// <summary>Creates a fixed snapshot with additional internal descriptors hidden from Providers.</summary>
    public ToolCatalogSnapshot WithInternalTools(IEnumerable<ToolDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        return new ToolCatalogSnapshot(
            HostCatalogVersion,
            EffectiveVersion,
            _allTools.Concat(descriptors));
    }

    /// <summary>Looks up a tool within this fixed snapshot.</summary>
    public bool TryGetTool(string toolId, out ToolDescriptor? descriptor) =>
        _tools.TryGetValue(toolId, out descriptor);

    /// <summary>按模式过滤工具目录。</summary>
    /// <param name="mode">模式名称：expert / work / meeting</param>
    /// <param name="agentRole">Agent 角色（Work/Meeting 模式需要）：general-manager / project-lead / meeting-host / participant</param>
    public ToolCatalogSnapshot FilterByMode(string mode, string? agentRole = null)
    {
        var filtered = _allTools.Where(tool =>
        {
            // 宿主工具或通用工具：不过滤（SupportedModes == null）
            if (tool.SupportedModes is null || tool.SupportedModes.Count == 0)
                return true;

            // 检查是否支持当前模式
            if (!tool.SupportedModes.Contains(mode, StringComparer.OrdinalIgnoreCase))
                return false;

            // Work 模式需要按角色过滤
            if (string.Equals(mode, "work", StringComparison.OrdinalIgnoreCase) && agentRole is not null)
                return IsAllowedForWorkRole(tool, agentRole);

            // Meeting 模式需要按角色过滤
            if (string.Equals(mode, "meeting", StringComparison.OrdinalIgnoreCase) && agentRole is not null)
                return IsAllowedForMeetingRole(tool, agentRole);

            return true;
        }).ToArray();

        return new ToolCatalogSnapshot(HostCatalogVersion, EffectiveVersion, filtered);
    }

    /// <summary>Work 模式按角色过滤（参考工作与会议模式.md §1）。</summary>
    private static bool IsAllowedForWorkRole(ToolDescriptor tool, string agentRole)
    {
        // general-manager (A): 所有 Work 工具
        if (string.Equals(agentRole, "general-manager", StringComparison.OrdinalIgnoreCase))
            return true;

        // project-lead (B): 只能用 dispatch_step + read_template_file，不能 ask_user / set_plan
        if (string.Equals(agentRole, "project-lead", StringComparison.OrdinalIgnoreCase))
        {
            return tool.ToolId switch
            {
                "mcli_ask_user" => false,  // B 不能问用户
                "mcli_work_set_plan" => false,
                "mcli_work_list_plan_templates" => false,
                "mcli_work_load_plan_from_template" => false,
                "mcli_work_load_plan_from_chat_history" => false,
                "mcli_work_load_plan_from_groupchat" => false,
                "mcli_work_get_recent_completed_task_plan" => false,
                "mcli_work_dispatch_step" => true,  // B 可以执行步骤
                "mcli_work_read_template_file" => true,  // B 可以读模板文件
                _ => true  // 其他通用工具
            };
        }

        return true;
    }

    /// <summary>Meeting 模式按角色过滤（参考工作与会议模式.md §1）。</summary>
    private static bool IsAllowedForMeetingRole(ToolDescriptor tool, string agentRole)
    {
        // meeting-host: 所有 Meeting 工具
        if (string.Equals(agentRole, "meeting-host", StringComparison.OrdinalIgnoreCase))
            return true;

        // 参会者 (participant): 只能 list_attachments，不能 output_summary / confirm_end / ask_user
        return tool.ToolId switch
        {
            "mcli_meeting_output_summary" => false,
            "mcli_meeting_confirm_end" => false,
            "mcli_ask_user" => false,
            "mcli_check_pending_user_input" => false,
            "mcli_meeting_list_attachments" => true,
            _ => true  // 其他通用工具
        };
    }

    private static ToolDescriptor CloneDescriptor(ToolDescriptor descriptor) =>
        descriptor with
        {
            Tags = descriptor.Tags is null
                ? Array.Empty<string>()
                : Array.AsReadOnly(descriptor.Tags.ToArray()),
            Capabilities = descriptor.Capabilities is null
                ? Array.Empty<string>()
                : Array.AsReadOnly(descriptor.Capabilities.ToArray())
        };
}
