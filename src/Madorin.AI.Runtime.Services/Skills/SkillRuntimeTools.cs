using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Services.Skills;

/// <summary>
/// Model-facing Runtime tools that replace MAF <c>load_skill</c> / <c>read_skill_resource</c> /
/// <c>run_skill_script</c> after Provider Workers receive null Skills.
/// </summary>
public static class SkillRuntimeTools
{
    public const string LoadToolId = "runtime.skill.load";
    public const string ReadToolId = "runtime.skill.read";
    public const string ReadResourceToolId = "runtime.skill.read_resource";
    public const string RunToolId = "runtime.skill.run_script";

    public static ToolDescriptor Load { get; } = new(
        LoadToolId,
        "runtime.skill",
        "load_skill_alias",
        "Loads one Skill's SKILL.md into this turn. Use the returned body; do not invent paths.",
        """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"],"additionalProperties":false}""",
        """{"type":"object","properties":{"name":{"type":"string"},"content":{"type":"string"}},"required":["name","content"],"additionalProperties":false}""",
        Tags: ["internal"],
        Capabilities: ["skill.load"],
        Risk: ToolRiskLevel.SensitiveRead,
        ExecutionTarget: ToolExecutionTarget.Builtin);

    public static ToolDescriptor Read { get; } = new(
        ReadToolId,
        "runtime.skill",
        "load_skill",
        "Loads one Skill's SKILL.md, or one declared resource when resource is set.",
        """{"type":"object","properties":{"name":{"type":"string"},"resource":{"type":"string"}},"required":["name"],"additionalProperties":false}""",
        """{"type":"object","properties":{"name":{"type":"string"},"resource":{"type":["string","null"]},"content":{"type":"string"}},"required":["name","content"],"additionalProperties":false}""",
        Capabilities: ["skill.load", "skill.resource"],
        Risk: ToolRiskLevel.SensitiveRead,
        ExecutionTarget: ToolExecutionTarget.Builtin);

    public static ToolDescriptor ReadResource { get; } = new(
        ReadResourceToolId,
        "runtime.skill",
        "read_skill_resource",
        "Reads one declared resource file from a loaded Skill snapshot.",
        """{"type":"object","properties":{"name":{"type":"string"},"resource":{"type":"string"}},"required":["name","resource"],"additionalProperties":false}""",
        """{"type":"object","properties":{"name":{"type":"string"},"resource":{"type":"string"},"content":{"type":"string"}},"required":["name","resource","content"],"additionalProperties":false}""",
        Tags: ["internal"],
        Capabilities: ["skill.resource"],
        Risk: ToolRiskLevel.SensitiveRead,
        ExecutionTarget: ToolExecutionTarget.Builtin);

    public static ToolDescriptor Run { get; } = new(
        RunToolId,
        "runtime.skill",
        "run_skill_script",
        "Runs one declared script from an immutable Skill snapshot.",
        """{"type":"object","properties":{"skillName":{"type":"string"},"script":{"type":"string"},"arguments":{"type":"array","items":{"type":"string"}}},"required":["skillName","script"],"additionalProperties":false}""",
        """{"type":"object","properties":{"skillName":{"type":"string"},"script":{"type":"string"},"interpreter":{"type":"string"},"exitCode":{"type":"integer"},"stdout":{"type":"string"},"stderr":{"type":"string"}},"required":["skillName","script","interpreter","exitCode","stdout","stderr"],"additionalProperties":false}""",
        RiskLevel: "process",
        RequiresApproval: true,
        TimeoutSeconds: 300,
        Capabilities: ["skill.script"],
        Risk: ToolRiskLevel.Process,
        ExecutionTarget: ToolExecutionTarget.Builtin,
        IsIdempotent: false);

    public static IReadOnlyList<ToolDescriptor> Advertised { get; } = [Read, Run];

    public static IReadOnlyList<ToolDescriptor> GatewayDescriptors { get; } =
        [SkillScriptToolDefinition.Descriptor, Read, Run, Load, ReadResource];

    public static bool IsSkillRuntimeTool(string toolId) =>
        string.Equals(toolId, LoadToolId, StringComparison.Ordinal)
        || string.Equals(toolId, ReadToolId, StringComparison.Ordinal)
        || string.Equals(toolId, ReadResourceToolId, StringComparison.Ordinal)
        || string.Equals(toolId, RunToolId, StringComparison.Ordinal);
}
