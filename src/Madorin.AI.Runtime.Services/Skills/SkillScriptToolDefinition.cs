using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Services.Skills;

/// <summary>Internal Gateway-only descriptor for MAF Skill script execution.</summary>
public static class SkillScriptToolDefinition
{
    public static ToolDescriptor Descriptor { get; } = new(
        SkillScriptAuthorization.ToolId,
        "builtin.skill",
        "Run Skill script",
        "Runs one declared script from an immutable Skill snapshot.",
        """{"type":"object","properties":{"skillName":{"type":"string"},"script":{"type":"string"},"scriptHash":{"type":"string"},"interpreter":{"type":"string"},"catalogVersion":{"type":"string"},"arguments":{"type":"array","items":{"type":"string"}},"timeoutMilliseconds":{"type":"integer","minimum":1,"maximum":3600000}},"required":["skillName","script","scriptHash","interpreter","catalogVersion"],"additionalProperties":false}""",
        """{"type":"object","properties":{"skillName":{"type":"string"},"script":{"type":"string"},"interpreter":{"type":"string"},"catalogVersion":{"type":"string"},"exitCode":{"type":"integer"},"stdout":{"type":"string"},"stderr":{"type":"string"},"durationMilliseconds":{"type":"number"}},"required":["skillName","script","interpreter","catalogVersion","exitCode","stdout","stderr","durationMilliseconds"],"additionalProperties":false}""",
        RiskLevel: "process",
        RequiresApproval: true,
        TimeoutSeconds: 300,
        Tags: ["internal", "skills"],
        Capabilities: ["skill.script"],
        Risk: ToolRiskLevel.Process,
        ExecutionTarget: ToolExecutionTarget.Builtin,
        IsIdempotent: false);

    public static bool IsInternal(ToolDescriptor descriptor) =>
        descriptor.Tags?.Contains("internal", StringComparer.Ordinal) == true;
}
