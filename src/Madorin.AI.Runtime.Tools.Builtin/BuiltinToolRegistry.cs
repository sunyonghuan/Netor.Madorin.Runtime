using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Tools.Builtin;

public sealed class BuiltinToolRegistry : IBuiltinToolRegistry
{
    public const string MemoryReadToolId = "mcli_memory_read";
    public const string MemoryWriteToolId = "mcli_memory_write";
    public const string MemoryAppendToolId = "mcli_memory_append";
    public const string MemoryUpdateToolId = "mcli_memory_update";
    public const string MemoryDeleteToolId = "mcli_memory_delete";
    public const string FileInspectToolId = "mcli_fs_inspect";
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
    public const string DelegateToAgentToolId = "mcli_delegate_agent";
    public const string WorkListPlanTemplatesToolId = "mcli_work_list_plan_templates";
    public const string WorkLoadPlanFromTemplateToolId = "mcli_work_load_plan_from_template";
    public const string WorkReadTemplateFileToolId = "mcli_work_read_template_file";
    public const string WorkSetPlanToolId = "mcli_work_set_plan";
    public const string WorkDispatchStepToolId = "mcli_work_dispatch_step";
    public const string WorkLoadPlanFromChatHistoryToolId = "mcli_work_load_plan_from_chat_history";
    public const string WorkLoadPlanFromGroupchatToolId = "mcli_work_load_plan_from_groupchat";
    public const string WorkGetRecentCompletedTaskPlanToolId = "mcli_work_get_recent_completed_task_plan";
    public const string AskUserToolId = "mcli_ask_user";
    public const string CheckPendingUserInputToolId = "mcli_check_pending_user_input";

    private static readonly IReadOnlyList<string> WorkOnlyModes = Array.AsReadOnly(["work"]);
    private static readonly IReadOnlyList<string> WorkAndMeetingModes = Array.AsReadOnly(["work", "meeting"]);

    private static readonly IReadOnlyList<ToolDescriptor> AdvertisedTools = Array.AsReadOnly<ToolDescriptor>(
    [
        new(
            MemoryReadToolId,
            "mcli",
            "Read memory",
            "Reads every line of global or project memory with line numbers and a hash.",
            """{"type":"object","properties":{"scope":{"type":"string","enum":["global","project"]}},"required":["scope"],"additionalProperties":false}""",
            """{"type":"object","properties":{"scope":{"type":"string"},"lineCount":{"type":"integer"},"lines":{"type":"array","items":{"type":"object","properties":{"line":{"type":"integer"},"text":{"type":"string"}},"required":["line","text"],"additionalProperties":false}},"hash":{"type":["string","null"]}},"required":["scope","lineCount","lines","hash"],"additionalProperties":false}""",
            Capabilities: ["memory.read"]),
        new(
            MemoryWriteToolId,
            "mcli",
            "Write memory",
            "Appends, replaces, or deletes lines in global or project memory. Read first and pass its hash.",
            """{"type":"object","properties":{"scope":{"type":"string","enum":["global","project"]},"operation":{"type":"string","enum":["append","update","delete"]},"lines":{"type":"array","minItems":1,"items":{"type":"string","minLength":1}},"startLine":{"type":"integer","minimum":1},"endLine":{"type":"integer","minimum":1},"expectedHash":{"type":["string","null"]}},"required":["scope","operation","expectedHash"],"additionalProperties":false,"allOf":[{"if":{"properties":{"operation":{"const":"append"}},"required":["operation"]},"then":{"required":["lines"]}},{"if":{"properties":{"operation":{"const":"update"}},"required":["operation"]},"then":{"required":["startLine","endLine","lines"]}},{"if":{"properties":{"operation":{"const":"delete"}},"required":["operation"]},"then":{"required":["startLine","endLine"]}}]}""",
            """{"type":"object","properties":{"written":{"type":"boolean"},"operation":{"type":"string"},"lineCount":{"type":"integer"},"hash":{"type":"string"},"appliesFrom":{"type":"string","const":"nextInvocation"}},"required":["written","operation","lineCount","hash","appliesFrom"],"additionalProperties":false}""",
            RiskLevel: "medium",
            RequiresApproval: true,
            Capabilities: ["memory.write"],
            Risk: ToolRiskLevel.Write),
        new(
            FileInspectToolId,
            "mcli",
            "Inspect files",
            "Lists a directory or returns path metadata.",
            """{"type":"object","properties":{"mode":{"type":"string","enum":["list","stat"]},"path":{"type":"string"},"pattern":{"type":"string"},"recursive":{"type":"boolean"},"maxDepth":{"type":"integer","minimum":0,"maximum":32},"limit":{"type":"integer","minimum":1,"maximum":10000}},"required":["mode"],"additionalProperties":false,"allOf":[{"if":{"properties":{"mode":{"const":"stat"}},"required":["mode"]},"then":{"required":["path"]}}]}""",
            """{"oneOf":[{"type":"object","properties":{"entries":{"type":"array"},"truncated":{"type":"boolean"}},"required":["entries","truncated"],"additionalProperties":false},{"type":"object","properties":{"exists":{"type":"boolean"},"kind":{"type":["string","null"]},"contentKind":{"type":["string","null"]},"byteLength":{"type":["integer","null"]},"lastWriteTime":{"type":["string","null"]},"sha256":{"type":["string","null"]}},"required":["exists","kind","contentKind","byteLength","lastWriteTime","sha256"],"additionalProperties":false}]}""",
            Capabilities: ["filesystem.read"],
            Risk: ToolRiskLevel.SensitiveRead),
        new(
            FileReadToolId,
            "mcli",
            "Read file",
            "Reads a text file by line range.",
            """{"type":"object","properties":{"path":{"type":"string"},"encoding":{"type":"string","enum":["auto","utf-8","utf-16le","utf-16be","gb2312","gbk","gb18030","ascii","latin1"]},"startLine":{"type":"integer","minimum":1},"endLine":{"type":"integer","minimum":1}},"required":["path","encoding"],"additionalProperties":false}""",
            """{"type":"object","properties":{"content":{"type":"string"},"encoding":{"type":"string"},"codePage":{"type":"integer"},"byteLength":{"type":"integer"},"startLine":{"type":"integer"},"endLine":{"type":"integer"},"totalLines":{"type":"integer"},"sha256":{"type":"string"},"hasBom":{"type":"boolean"},"lineEnding":{"type":"string"},"endsWithNewline":{"type":"boolean"},"truncated":{"type":"boolean"}},"required":["content","encoding","codePage","byteLength","startLine","endLine","totalLines","sha256","hasBom","lineEnding","endsWithNewline","truncated"],"additionalProperties":false}""",
            Capabilities: ["filesystem.read"],
            Risk: ToolRiskLevel.SensitiveRead),
        new(
            FileWriteToolId,
            "mcli",
            "Write file",
            "Creates, overwrites, or appends a text file, or creates a directory.",
            """{"type":"object","properties":{"path":{"type":"string"},"mode":{"type":"string","enum":["create","overwrite","append","mkdir"]},"content":{"type":"string"},"encoding":{"type":"string","enum":["utf-8","utf-16le","utf-16be","gb2312","gbk","gb18030","ascii","latin1"]},"expectedHash":{"type":"string"},"bom":{"type":"string","enum":["preserve","add","remove"]},"lineEnding":{"type":"string","enum":["preserve","lf","crlf"]},"createParents":{"type":"boolean"}},"required":["path","mode"],"additionalProperties":false,"allOf":[{"if":{"properties":{"mode":{"enum":["create","overwrite","append"]}},"required":["mode"]},"then":{"required":["content","encoding"]}}]}""",
            """{"oneOf":[{"type":"object","properties":{"written":{"type":"boolean"},"mode":{"type":"string"},"byteLength":{"type":["integer","null"]},"sha256":{"type":["string","null"]}},"required":["written","mode","byteLength","sha256"],"additionalProperties":false},{"type":"object","properties":{"written":{"type":"boolean"},"byteLength":{"type":"integer"},"sha256":{"type":"string"}},"required":["written","byteLength","sha256"],"additionalProperties":false}]}""",
            RiskLevel: "medium",
            Capabilities: ["filesystem.write"],
            Risk: ToolRiskLevel.Write,
            IsIdempotent: false),
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
            """{"type":"object","properties":{"path":{"type":"string"},"query":{"type":"string"},"encoding":{"type":"string","enum":["auto","utf-8","utf-16le","utf-16be","gb2312","gbk","gb18030","ascii","latin1"]},"pattern":{"type":"string"},"caseSensitive":{"type":"boolean"},"maxResults":{"type":"integer","minimum":1,"maximum":100}},"required":["path","query","encoding"],"additionalProperties":false}""",
            """{"type":"object","properties":{"matches":{"type":"array"}},"required":["matches"],"additionalProperties":false}""",
            Capabilities: ["filesystem.read"],
            Risk: ToolRiskLevel.SensitiveRead),
        new(
            FileEditToolId,
            "mcli",
            "Edit file",
            "Applies line edits to a text file.",
            """{"type":"object","properties":{"path":{"type":"string"},"encoding":{"type":"string","enum":["utf-8","utf-16le","utf-16be","gb2312","gbk","gb18030","ascii","latin1"]},"expectedHash":{"type":"string"},"operations":{"type":"array","minItems":1,"maxItems":100,"items":{"type":"object"}}},"required":["path","encoding","expectedHash","operations"],"additionalProperties":false}""",
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
            IsIdempotent: false),
        new(
            DelegateToAgentToolId,
            "mcli",
            "Delegate to agent",
            "Delegates a task to a specific agent from the available agent pool.",
            """{"type":"object","properties":{"agentName":{"type":"string","description":"Name of the target agent (case-insensitive)"},"task":{"type":"string","description":"Task description to delegate"}},"required":["agentName","task"],"additionalProperties":false}""",
            """{"type":"object","properties":{"delegated":{"type":"boolean"},"agentName":{"type":"string"},"task":{"type":"string"},"message":{"type":"string"}},"required":["delegated","message"],"additionalProperties":false}""",
            RiskLevel: "low",
            Capabilities: ["agent.delegate"],
            Risk: ToolRiskLevel.Low,
            IsIdempotent: false),
        new(
            WorkListPlanTemplatesToolId,
            "mcli",
            "List work plan templates",
            "Lists valid work-plan templates under user/system work-templates roots. Empty list is success.",
            """{"type":"object","properties":{"keyword":{"type":"string"},"category":{"type":"string"}},"additionalProperties":false}""",
            """{"type":"object","properties":{"ok":{"type":"boolean"},"templates":{"type":"array","items":{"type":"object","properties":{"name":{"type":"string"},"description":{"type":"string"},"path":{"type":"string"},"category":{"type":"string"},"version":{"type":"string"}},"required":["name","description","path","category","version"],"additionalProperties":false}}},"required":["ok","templates"],"additionalProperties":false}""",
            Capabilities: ["work.templates"],
            Risk: ToolRiskLevel.SensitiveRead,
            SupportedModes: WorkOnlyModes),
        new(
            WorkLoadPlanFromTemplateToolId,
            "mcli",
            "Load work plan template",
            "Loads TEMPLATE.md materials from an absolute template directory path. Does not call set_plan.",
            """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}""",
            """{"type":"object","properties":{"ok":{"type":"boolean"},"error":{"type":"string"},"message":{"type":"string"},"path":{"type":"string"},"name":{"type":"string"},"description":{"type":"string"},"category":{"type":"string"},"version":{"type":"string"},"instructions":{"type":"string"},"planSkeleton":{"type":"string"},"files":{"type":"array"},"note":{"type":"string"}},"required":["ok"],"additionalProperties":false}""",
            Capabilities: ["work.templates"],
            Risk: ToolRiskLevel.SensitiveRead,
            SupportedModes: WorkOnlyModes),
        new(
            WorkReadTemplateFileToolId,
            "mcli",
            "Read work template file",
            "Reads one indexed UTF-8 file inside a work-plan template directory.",
            """{"type":"object","properties":{"path":{"type":"string"},"relativePath":{"type":"string"}},"required":["path","relativePath"],"additionalProperties":false}""",
            """{"type":"object","properties":{"ok":{"type":"boolean"},"error":{"type":"string"},"message":{"type":"string"},"path":{"type":"string"},"relativePath":{"type":"string"},"content":{"type":"string"}},"required":["ok"],"additionalProperties":false}""",
            Capabilities: ["work.templates"],
            Risk: ToolRiskLevel.SensitiveRead,
            SupportedModes: WorkOnlyModes),
        new(
            WorkSetPlanToolId,
            "mcli",
            "Set work plan",
            "Validates and returns an executable WorkPlanDraft (native planVersion/goal/steps or main_steps/sub_steps). Runtime persists it and waits for user confirmation before dispatch.",
            """{"type":"object","properties":{"planVersion":{"type":"string"},"goal":{"type":"string"},"steps":{"type":"array"},"main_steps":{"type":"array"},"previousPlanVersion":{"type":"string"},"summary":{"type":"string"},"plan":{"type":"object"}},"additionalProperties":true}""",
            """{"type":"object","properties":{"ok":{"type":"boolean"},"error":{"type":"string"},"message":{"type":"string"},"planVersion":{"type":"string"},"goal":{"type":"string"},"stepCount":{"type":"integer"},"plan":{"type":"object"},"requiresConfirmation":{"type":"boolean"}},"required":["ok"],"additionalProperties":false}""",
            RiskLevel: "medium",
            RequiresApproval: false,
            Capabilities: ["work.plan"],
            Risk: ToolRiskLevel.Write,
            IsIdempotent: false,
            SupportedModes: WorkOnlyModes),
        new(
            WorkDispatchStepToolId,
            "mcli",
            "Dispatch work step",
            "Validates one confirmed work sub-step and synchronously runs the target agent through the Work orchestrator. Rejects when plan is missing or waiting for confirmation.",
            """{"type":"object","properties":{"stepIndex":{"type":"integer","minimum":1},"subStepIndex":{"type":"integer","minimum":1},"instruction":{"type":"string"}},"required":["stepIndex","subStepIndex","instruction"],"additionalProperties":false}""",
            """{"type":"object","properties":{"ok":{"type":"boolean"},"executed":{"type":"boolean"},"error":{"type":"string"},"message":{"type":"string"},"planVersion":{"type":"string"},"stepId":{"type":"string"},"targetAgentId":{"type":"string"},"goal":{"type":"string"},"instruction":{"type":"string"},"childInvocationId":{"type":"string"},"outputText":{"type":"string"}},"required":["ok"],"additionalProperties":false}""",
            RiskLevel: "medium",
            Capabilities: ["work.dispatch"],
            Risk: ToolRiskLevel.Write,
            IsIdempotent: false,
            SupportedModes: WorkOnlyModes),
        new(
            WorkLoadPlanFromChatHistoryToolId,
            "mcli",
            "Load plan from chat history",
            "Loads recent chat/session transcript as planning materials. Still requires mcli_work_set_plan to publish.",
            """{"type":"object","properties":{"sessionId":{"type":"string"},"limit":{"type":"integer","minimum":1}},"additionalProperties":false}""",
            """{"type":"object","properties":{"ok":{"type":"boolean"},"source":{"type":"string"},"sessionId":{"type":"string"},"count":{"type":"integer"},"messages":{"type":"array"},"note":{"type":"string"},"error":{"type":"string"},"message":{"type":"string"}},"required":["ok"],"additionalProperties":false}""",
            Capabilities: ["work.history"],
            Risk: ToolRiskLevel.SensitiveRead,
            SupportedModes: WorkOnlyModes),
        new(
            WorkLoadPlanFromGroupchatToolId,
            "mcli",
            "Load plan from groupchat",
            "Loads groupchat/meeting transcript as planning materials. Still requires mcli_work_set_plan to publish.",
            """{"type":"object","properties":{"meetingId":{"type":"string"},"limit":{"type":"integer","minimum":1}},"additionalProperties":false}""",
            """{"type":"object","properties":{"ok":{"type":"boolean"},"source":{"type":"string"},"sessionId":{"type":"string"},"count":{"type":"integer"},"messages":{"type":"array"},"note":{"type":"string"},"error":{"type":"string"},"message":{"type":"string"}},"required":["ok"],"additionalProperties":false}""",
            Capabilities: ["work.history"],
            Risk: ToolRiskLevel.SensitiveRead,
            SupportedModes: WorkOnlyModes),
        new(
            WorkGetRecentCompletedTaskPlanToolId,
            "mcli",
            "Get recent completed task plan",
            "Returns the current session plan when the Work session is completed, as materials for redo/revise. Still requires mcli_work_set_plan.",
            """{"type":"object","properties":{},"additionalProperties":false}""",
            """{"type":"object","properties":{"ok":{"type":"boolean"},"found":{"type":"boolean"},"status":{"type":"string"},"planVersion":{"type":"string"},"plan":{"type":"object"},"note":{"type":"string"},"message":{"type":"string"},"error":{"type":"string"}},"required":["ok"],"additionalProperties":false}""",
            Capabilities: ["work.history"],
            Risk: ToolRiskLevel.SensitiveRead,
            SupportedModes: WorkOnlyModes),
        new(
            AskUserToolId,
            "mcli",
            "Ask user",
            "Asks the host/user a short decision question and waits for the answer. Call mcli_check_pending_user_input first. Do not invent the answer.",
            """{"type":"object","properties":{"question":{"type":"string"}},"required":["question"],"additionalProperties":false}""",
            """{"type":"object","properties":{"ok":{"type":"boolean"},"answered":{"type":"boolean"},"question":{"type":"string"},"answer":{"type":"string"},"error":{"type":"string"},"message":{"type":"string"}},"required":["ok"],"additionalProperties":false}""",
            RiskLevel: "medium",
            RequiresApproval: false,
            Capabilities: ["mode.ask_user"],
            Risk: ToolRiskLevel.Write,
            IsIdempotent: false,
            SupportedModes: WorkAndMeetingModes),
        new(
            CheckPendingUserInputToolId,
            "mcli",
            "Check pending user input",
            "Checks unread pending user inputs for the current session. Work consumes FIFO; Meeting peeks. Call before mcli_ask_user.",
            """{"type":"object","properties":{},"additionalProperties":false}""",
            """{"type":"object","properties":{"ok":{"type":"boolean"},"hasPending":{"type":"boolean"},"consumed":{"type":"boolean"},"inputs":{"type":"array","items":{"type":"string"}},"count":{"type":"integer"},"error":{"type":"string"},"message":{"type":"string"}},"required":["ok"],"additionalProperties":false}""",
            Capabilities: ["mode.pending_input"],
            Risk: ToolRiskLevel.SensitiveRead,
            IsIdempotent: false,
            SupportedModes: WorkAndMeetingModes)
    ]);

    private static readonly IReadOnlyList<ToolDescriptor> Tools = Array.AsReadOnly(
        [.. AdvertisedTools, .. BuiltinToolAliasCatalog.Tools]);

    public string CatalogVersion => "3.3.0";

    public IReadOnlyList<ToolDescriptor> GetTools() => Tools;

    public IReadOnlyList<ToolDescriptor> GetTools(string[] toolIds)
    {
        ArgumentNullException.ThrowIfNull(toolIds);
        var requested = toolIds.ToHashSet(StringComparer.Ordinal);
        return Tools.Where(tool => requested.Contains(tool.ToolId)).ToArray();
    }
}
