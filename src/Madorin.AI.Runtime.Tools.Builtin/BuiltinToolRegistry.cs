using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Tools.Builtin;

public sealed class BuiltinToolRegistry : IBuiltinToolRegistry
{
    public const string MemoryReadToolId = "mcli_memory_read";
    public const string MemoryAppendToolId = "mcli_memory_append";
    public const string FileListToolId = "mcli_fs_list";
    public const string FileStatusToolId = "mcli_fs_stat";
    public const string FileReadToolId = "mcli_fs_read";
    public const string FileWriteToolId = "mcli_fs_write";
    public const string FileAppendToolId = "mcli_fs_append";
    public const string DirectoryCreateToolId = "mcli_fs_mkdir";
    public const string FileCopyToolId = "mcli_fs_copy";
    public const string FileMoveToolId = "mcli_fs_move";
    public const string FileDeleteToolId = "mcli_fs_delete";
    public const string FileSearchToolId = "mcli_fs_search";
    public const string FileEditToolId = "mcli_fs_edit";
    public const string ProcessRunToolId = "mcli_process";
    public const string HttpRequestToolId = "mcli_http";

    private static readonly IReadOnlyList<ToolDescriptor> Tools = Array.AsReadOnly<ToolDescriptor>(
    [
        new(
            MemoryReadToolId,
            "mcli",
            "Read memory",
            "Reads Runtime memory.",
            """{"type":"object","properties":{"scope":{"type":"string","enum":["global","project","effective"]}},"required":["scope"],"additionalProperties":false}""",
            """{"type":"object","properties":{"content":{"type":["string","null"]},"hash":{"type":["string","null"]}},"required":["content","hash"],"additionalProperties":false}""",
            Capabilities: ["memory.read"]),
        new(
            MemoryAppendToolId,
            "mcli",
            "Append memory",
            "Appends an item to Runtime memory.",
            """{"type":"object","properties":{"scope":{"type":"string","enum":["global","project"]},"item":{"type":"string"}},"required":["scope","item"],"additionalProperties":false}""",
            """{"type":"object","properties":{"appended":{"type":"boolean"},"hash":{"type":"string"},"appliesFrom":{"type":"string","const":"nextInvocation"}},"required":["appended","hash","appliesFrom"],"additionalProperties":false}""",
            RiskLevel: "medium",
            RequiresApproval: true,
            Capabilities: ["memory.write"],
            Risk: ToolRiskLevel.Write),
        new(
            FileListToolId,
            "mcli",
            "List files",
            "Lists files and directories.",
            """{"type":"object","properties":{"path":{"type":"string"},"pattern":{"type":"string"},"recursive":{"type":"boolean"},"maxDepth":{"type":"integer","minimum":0,"maximum":32},"limit":{"type":"integer","minimum":1,"maximum":10000}},"additionalProperties":false}""",
            """{"type":"object","properties":{"entries":{"type":"array"},"truncated":{"type":"boolean"}},"required":["entries","truncated"],"additionalProperties":false}""",
            Capabilities: ["filesystem.read"],
            Risk: ToolRiskLevel.SensitiveRead),
        new(
            FileStatusToolId,
            "mcli",
            "Inspect path",
            "Gets file or directory metadata.",
            """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}""",
            """{"type":"object","properties":{"exists":{"type":"boolean"},"kind":{"type":["string","null"]},"contentKind":{"type":["string","null"]},"byteLength":{"type":["integer","null"]},"lastWriteTime":{"type":["string","null"]},"sha256":{"type":["string","null"]}},"required":["exists","kind","contentKind","byteLength","lastWriteTime","sha256"],"additionalProperties":false}""",
            Capabilities: ["filesystem.read"],
            Risk: ToolRiskLevel.SensitiveRead),
        new(
            FileReadToolId,
            "mcli",
            "Read file",
            "Reads a text file by line range.",
            """{"type":"object","properties":{"path":{"type":"string"},"encoding":{"type":"string"},"startLine":{"type":"integer","minimum":1},"endLine":{"type":"integer","minimum":1}},"required":["path","encoding"],"additionalProperties":false}""",
            """{"type":"object","properties":{"content":{"type":"string"},"encoding":{"type":"string"},"codePage":{"type":"integer"},"byteLength":{"type":"integer"},"startLine":{"type":"integer"},"endLine":{"type":"integer"},"totalLines":{"type":"integer"},"sha256":{"type":"string"},"hasBom":{"type":"boolean"},"lineEnding":{"type":"string"},"endsWithNewline":{"type":"boolean"},"truncated":{"type":"boolean"}},"required":["content","encoding","codePage","byteLength","startLine","endLine","totalLines","sha256","hasBom","lineEnding","endsWithNewline","truncated"],"additionalProperties":false}""",
            Capabilities: ["filesystem.read"],
            Risk: ToolRiskLevel.SensitiveRead),
        new(
            FileWriteToolId,
            "mcli",
            "Write file",
            "Creates or replaces a text file.",
            """{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"},"encoding":{"type":"string"},"mode":{"type":"string","enum":["create","overwrite"]},"expectedHash":{"type":"string"},"bom":{"type":"string","enum":["preserve","add","remove"]},"lineEnding":{"type":"string","enum":["preserve","lf","crlf"]}},"required":["path","content","encoding","mode"],"additionalProperties":false}""",
            """{"type":"object","properties":{"written":{"type":"boolean"},"byteLength":{"type":"integer"},"sha256":{"type":"string"}},"required":["written","byteLength","sha256"],"additionalProperties":false}""",
            RiskLevel: "medium",
            Capabilities: ["filesystem.write"],
            Risk: ToolRiskLevel.Write,
            IsIdempotent: false),
        new(
            FileAppendToolId,
            "mcli",
            "Append file",
            "Appends text to a file.",
            """{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"},"encoding":{"type":"string"},"expectedHash":{"type":"string"}},"required":["path","content","encoding","expectedHash"],"additionalProperties":false}""",
            """{"type":"object","properties":{"appended":{"type":"boolean"},"byteLength":{"type":"integer"},"sha256":{"type":"string"}},"required":["appended","byteLength","sha256"],"additionalProperties":false}""",
            RiskLevel: "medium",
            Capabilities: ["filesystem.read", "filesystem.write"],
            Risk: ToolRiskLevel.Write,
            IsIdempotent: false),
        new(
            DirectoryCreateToolId,
            "mcli",
            "Create directory",
            "Creates a directory.",
            """{"type":"object","properties":{"path":{"type":"string"},"createParents":{"type":"boolean"}},"required":["path"],"additionalProperties":false}""",
            """{"type":"object","properties":{"created":{"type":"boolean"}},"required":["created"],"additionalProperties":false}""",
            Capabilities: ["filesystem.write"],
            Risk: ToolRiskLevel.Write),
        new(
            FileCopyToolId,
            "mcli",
            "Copy path",
            "Copies a file or directory.",
            """{"type":"object","properties":{"source":{"type":"string"},"destination":{"type":"string"},"recursive":{"type":"boolean"}},"required":["source","destination"],"additionalProperties":false}""",
            """{"type":"object","properties":{"copied":{"type":"boolean"}},"required":["copied"],"additionalProperties":false}""",
            Capabilities: ["filesystem.read", "filesystem.write"],
            Risk: ToolRiskLevel.Write,
            IsIdempotent: false),
        new(
            FileMoveToolId,
            "mcli",
            "Move path",
            "Moves a file or directory.",
            """{"type":"object","properties":{"source":{"type":"string"},"destination":{"type":"string"}},"required":["source","destination"],"additionalProperties":false}""",
            """{"type":"object","properties":{"moved":{"type":"boolean"}},"required":["moved"],"additionalProperties":false}""",
            Capabilities: ["filesystem.write", "filesystem.destructive"],
            Risk: ToolRiskLevel.Destructive,
            IsIdempotent: false),
        new(
            FileDeleteToolId,
            "mcli",
            "Delete path",
            "Deletes a file or directory.",
            """{"type":"object","properties":{"path":{"type":"string"},"recursive":{"type":"boolean"}},"required":["path"],"additionalProperties":false}""",
            """{"type":"object","properties":{"deleted":{"type":"boolean"}},"required":["deleted"],"additionalProperties":false}""",
            Capabilities: ["filesystem.write", "filesystem.destructive"],
            Risk: ToolRiskLevel.Destructive,
            IsIdempotent: false),
        new(
            FileSearchToolId,
            "mcli",
            "Search text",
            "Searches text files.",
            """{"type":"object","properties":{"path":{"type":"string"},"query":{"type":"string"},"encoding":{"type":"string"},"pattern":{"type":"string"},"caseSensitive":{"type":"boolean"},"maxResults":{"type":"integer","minimum":1,"maximum":100}},"required":["path","query","encoding"],"additionalProperties":false}""",
            """{"type":"object","properties":{"matches":{"type":"array"}},"required":["matches"],"additionalProperties":false}""",
            Capabilities: ["filesystem.read"],
            Risk: ToolRiskLevel.SensitiveRead),
        new(
            FileEditToolId,
            "mcli",
            "Edit file",
            "Applies line edits to a text file.",
            """{"type":"object","properties":{"path":{"type":"string"},"encoding":{"type":"string"},"expectedHash":{"type":"string"},"operations":{"type":"array","minItems":1,"maxItems":100,"items":{"type":"object"}}},"required":["path","encoding","expectedHash","operations"],"additionalProperties":false}""",
            """{"type":"object","properties":{"edited":{"type":"boolean"},"byteLength":{"type":"integer"},"sha256":{"type":"string"}},"required":["edited","byteLength","sha256"],"additionalProperties":false}""",
            Capabilities: ["filesystem.read", "filesystem.write"],
            Risk: ToolRiskLevel.Write,
            IsIdempotent: false),
        new(
            ProcessRunToolId,
            "mcli",
            "Run process",
            "Runs an allowed executable.",
            """{"type":"object","properties":{"fileName":{"type":"string"},"arguments":{"type":"array","items":{"type":"string"}},"workingDirectory":{"type":"string"},"environment":{"type":"object","additionalProperties":{"type":"string"}},"stdin":{"type":"string"}},"required":["fileName"],"additionalProperties":false}""",
            """{"type":"object","properties":{"exitCode":{"type":"integer"},"stdout":{"type":"string"},"stderr":{"type":"string"},"stdoutTruncated":{"type":"boolean"},"stderrTruncated":{"type":"boolean"}},"required":["exitCode","stdout","stderr","stdoutTruncated","stderrTruncated"],"additionalProperties":false}""",
            Capabilities: ["process.execute"],
            Risk: ToolRiskLevel.Process,
            IsIdempotent: false),
        new(
            HttpRequestToolId,
            "mcli",
            "HTTP request",
            "Sends an allowed HTTP request.",
            """{"type":"object","properties":{"method":{"type":"string","enum":["GET","HEAD","POST","PUT","PATCH","DELETE"]},"url":{"type":"string"},"headers":{"type":"object","additionalProperties":{"type":"string"}},"body":{"type":"string"}},"required":["method","url"],"additionalProperties":false}""",
            """{"type":"object","properties":{"statusCode":{"type":"integer"},"headers":{"type":"object"},"body":{"type":"string"},"mediaType":{"type":["string","null"]}},"required":["statusCode","headers","body","mediaType"],"additionalProperties":false}""",
            Capabilities: ["network.access"],
            Risk: ToolRiskLevel.Network,
            IsIdempotent: false)
    ]);

    public string CatalogVersion => "2.0.0";

    public IReadOnlyList<ToolDescriptor> GetTools() => Tools;

    public IReadOnlyList<ToolDescriptor> GetTools(string[] toolIds)
    {
        ArgumentNullException.ThrowIfNull(toolIds);
        var requested = toolIds.ToHashSet(StringComparer.Ordinal);
        return Tools.Where(tool => requested.Contains(tool.ToolId)).ToArray();
    }
}
