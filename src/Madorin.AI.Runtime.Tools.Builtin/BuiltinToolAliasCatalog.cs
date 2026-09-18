using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Tools.Builtin;

/// <summary>
/// Catalog 3.0 合并前的内置工具描述符。Gateway 仍可按旧 ToolId 执行，但不广告给模型。
/// </summary>
internal static class BuiltinToolAliasCatalog
{
    internal static readonly string[] InternalTags = ["internal"];

    public static IReadOnlyList<ToolDescriptor> Tools { get; } = Array.AsReadOnly<ToolDescriptor>(
    [
        new(
            BuiltinToolRegistry.MemoryAppendToolId,
            "mcli",
            "Append memory",
            "Appends complete Markdown lines to global or project memory. Read first and pass its hash.",
            """{"type":"object","properties":{"scope":{"type":"string","enum":["global","project"]},"lines":{"type":"array","minItems":1,"items":{"type":"string","minLength":1}},"expectedHash":{"type":["string","null"]}},"required":["scope","lines","expectedHash"],"additionalProperties":false}""",
            """{"type":"object","properties":{"appended":{"type":"boolean"},"lineCount":{"type":"integer"},"hash":{"type":"string"},"appliesFrom":{"type":"string","const":"nextInvocation"}},"required":["appended","lineCount","hash","appliesFrom"],"additionalProperties":false}""",
            RiskLevel: "medium",
            RequiresApproval: true,
            Capabilities: ["memory.write"],
            Risk: ToolRiskLevel.Write,
            Tags: InternalTags),
        new(
            BuiltinToolRegistry.MemoryUpdateToolId,
            "mcli",
            "Update memory",
            "Replaces an inclusive line range in global or project memory. Read first and pass its hash.",
            """{"type":"object","properties":{"scope":{"type":"string","enum":["global","project"]},"startLine":{"type":"integer","minimum":1},"endLine":{"type":"integer","minimum":1},"lines":{"type":"array","minItems":1,"items":{"type":"string","minLength":1}},"expectedHash":{"type":"string"}},"required":["scope","startLine","endLine","lines","expectedHash"],"additionalProperties":false}""",
            """{"type":"object","properties":{"updated":{"type":"boolean"},"lineCount":{"type":"integer"},"hash":{"type":"string"},"appliesFrom":{"type":"string","const":"nextInvocation"}},"required":["updated","lineCount","hash","appliesFrom"],"additionalProperties":false}""",
            RiskLevel: "medium",
            RequiresApproval: true,
            Capabilities: ["memory.write"],
            Risk: ToolRiskLevel.Write,
            Tags: InternalTags),
        new(
            BuiltinToolRegistry.MemoryDeleteToolId,
            "mcli",
            "Delete memory",
            "Deletes an inclusive line range from global or project memory. Read first and pass its hash.",
            """{"type":"object","properties":{"scope":{"type":"string","enum":["global","project"]},"startLine":{"type":"integer","minimum":1},"endLine":{"type":"integer","minimum":1},"expectedHash":{"type":"string"}},"required":["scope","startLine","endLine","expectedHash"],"additionalProperties":false}""",
            """{"type":"object","properties":{"deleted":{"type":"boolean"},"lineCount":{"type":"integer"},"hash":{"type":"string"},"appliesFrom":{"type":"string","const":"nextInvocation"}},"required":["deleted","lineCount","hash","appliesFrom"],"additionalProperties":false}""",
            RiskLevel: "high",
            RequiresApproval: true,
            Capabilities: ["memory.write"],
            Risk: ToolRiskLevel.Destructive,
            Tags: InternalTags),
        new(
            BuiltinToolRegistry.FileListToolId,
            "mcli",
            "List files",
            "Lists files and directories.",
            """{"type":"object","properties":{"path":{"type":"string"},"pattern":{"type":"string"},"recursive":{"type":"boolean"},"maxDepth":{"type":"integer","minimum":0,"maximum":32},"limit":{"type":"integer","minimum":1,"maximum":10000}},"additionalProperties":false}""",
            """{"type":"object","properties":{"entries":{"type":"array"},"truncated":{"type":"boolean"}},"required":["entries","truncated"],"additionalProperties":false}""",
            Capabilities: ["filesystem.read"],
            Risk: ToolRiskLevel.SensitiveRead,
            Tags: InternalTags),
        new(
            BuiltinToolRegistry.FileStatusToolId,
            "mcli",
            "Inspect path",
            "Gets file or directory metadata.",
            """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}""",
            """{"type":"object","properties":{"exists":{"type":"boolean"},"kind":{"type":["string","null"]},"contentKind":{"type":["string","null"]},"byteLength":{"type":["integer","null"]},"lastWriteTime":{"type":["string","null"]},"sha256":{"type":["string","null"]}},"required":["exists","kind","contentKind","byteLength","lastWriteTime","sha256"],"additionalProperties":false}""",
            Capabilities: ["filesystem.read"],
            Risk: ToolRiskLevel.SensitiveRead,
            Tags: InternalTags),
        new(
            BuiltinToolRegistry.FileAppendToolId,
            "mcli",
            "Append file",
            "Appends text to a file.",
            """{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"},"encoding":{"type":"string","enum":["utf-8","utf-16le","utf-16be","gb2312","gbk","gb18030","ascii","latin1"]},"expectedHash":{"type":"string"}},"required":["path","content","encoding","expectedHash"],"additionalProperties":false}""",
            """{"type":"object","properties":{"appended":{"type":"boolean"},"byteLength":{"type":"integer"},"sha256":{"type":"string"}},"required":["appended","byteLength","sha256"],"additionalProperties":false}""",
            RiskLevel: "medium",
            Capabilities: ["filesystem.read", "filesystem.write"],
            Risk: ToolRiskLevel.Write,
            IsIdempotent: false,
            Tags: InternalTags),
        new(
            BuiltinToolRegistry.DirectoryCreateToolId,
            "mcli",
            "Create directory",
            "Creates a directory.",
            """{"type":"object","properties":{"path":{"type":"string"},"createParents":{"type":"boolean"}},"required":["path"],"additionalProperties":false}""",
            """{"type":"object","properties":{"created":{"type":"boolean"}},"required":["created"],"additionalProperties":false}""",
            Capabilities: ["filesystem.write"],
            Risk: ToolRiskLevel.Write,
            Tags: InternalTags)
    ]);
}
