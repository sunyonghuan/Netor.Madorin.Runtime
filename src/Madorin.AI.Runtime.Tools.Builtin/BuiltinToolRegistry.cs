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
    public const string AgentJobCreateToolId = "mcli_agent_job_create";
    public const string AgentJobCreateManyToolId = "mcli_agent_job_create_many";
    public const string AgentJobStatusToolId = "mcli_agent_job_status";
    public const string AgentJobResultToolId = "mcli_agent_job_result";
    public const string AgentJobWaitToolId = "mcli_agent_job_wait";
    public const string AgentJobCancelToolId = "mcli_agent_job_cancel";
    public const string MeetingOutputSummaryToolId = "mcli_meeting_output_summary";
    public const string MeetingConfirmEndToolId = "mcli_meeting_confirm_end";
    public const string MeetingListAttachmentsToolId = "mcli_meeting_list_attachments";
    public const string AskUserToolId = "mcli_ask_user";
    public const string CheckPendingUserInputToolId = "mcli_check_pending_user_input";
    public const string WorkSetPlanToolId = "mcli_work_set_plan";
    public const string ExpertCreatePlanToolId = "mcli_expert_create_plan";
    public const string ExpertExecuteStepToolId = "mcli_expert_execute_step";
    public const string ExpertGetPlanToolId = "mcli_expert_get_plan";
    public const string ExpertUpdateStepStatusToolId = "mcli_expert_update_step_status";
    public const string ExpertAbandonPlanToolId = "mcli_expert_abandon_plan";

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
            "Delegates a task to a specific agent from the Expert mode available agent pool. The Expert orchestrator runs the target agent and returns its output in result.",
            """{"type":"object","properties":{"agentName":{"type":"string","description":"Name of the target agent (case-insensitive)"},"task":{"type":"string","description":"Task description to delegate"}},"required":["agentName","task"],"additionalProperties":false}""",
            """{"type":"object","properties":{"delegated":{"type":"boolean"},"success":{"type":"boolean"},"agentName":{"type":"string"},"task":{"type":"string"},"message":{"type":"string"},"result":{"type":"string"}},"required":["delegated","success","message"],"additionalProperties":false}""",
            RiskLevel: "low",
            Capabilities: ["agent.delegate"],
            Risk: ToolRiskLevel.Low,
            IsIdempotent: false,
            SupportedModes: ["expert"]),
        new(
            AgentJobCreateToolId,
            "mcli",
            "Create agent job",
            "Creates one background parallel sub-agent job and returns jobId immediately. Use mcli_agent_job_result or mcli_agent_job_wait to recover the result.",
            """{"type":"object","properties":{"agentName":{"type":"string"},"task":{"type":"string"},"timeoutMs":{"type":"integer"}},"required":["agentName","task"],"additionalProperties":false}""",
            """{"type":"object","properties":{"success":{"type":"boolean"},"jobId":{"type":"string"},"state":{"type":"string"},"agentName":{"type":"string"},"task":{"type":"string"},"terminal":{"type":"boolean"},"message":{"type":"string"}},"required":["success","message"],"additionalProperties":false}""",
            RiskLevel: "low",
            Capabilities: ["agent.delegate"],
            Risk: ToolRiskLevel.Low,
            IsIdempotent: false,
            SupportedModes: ["expert"]),
        new(
            AgentJobCreateManyToolId,
            "mcli",
            "Create many agent jobs",
            "Creates multiple background parallel sub-agent jobs and returns jobIds immediately for later recovery.",
            """{"type":"object","properties":{"jobs":{"type":"array","items":{"type":"object","properties":{"agentName":{"type":"string"},"task":{"type":"string"},"timeoutMs":{"type":"integer"}},"required":["agentName","task"],"additionalProperties":false}}},"required":["jobs"],"additionalProperties":false}""",
            """{"type":"object","properties":{"success":{"type":"boolean"},"jobs":{"type":"array"},"message":{"type":"string"}},"required":["success","message"],"additionalProperties":false}""",
            RiskLevel: "low",
            Capabilities: ["agent.delegate"],
            Risk: ToolRiskLevel.Low,
            IsIdempotent: false,
            SupportedModes: ["expert"]),
        new(
            AgentJobStatusToolId,
            "mcli",
            "Agent job status",
            "Queries the state of a parallel sub-agent job.",
            """{"type":"object","properties":{"jobId":{"type":"string"}},"required":["jobId"],"additionalProperties":false}""",
            """{"type":"object","properties":{"success":{"type":"boolean"},"jobId":{"type":"string"},"state":{"type":"string"},"terminal":{"type":"boolean"},"message":{"type":"string"}},"required":["success","message"],"additionalProperties":false}""",
            RiskLevel: "low",
            Capabilities: ["agent.delegate"],
            Risk: ToolRiskLevel.Low,
            IsIdempotent: true,
            SupportedModes: ["expert"]),
        new(
            AgentJobResultToolId,
            "mcli",
            "Recover agent job result",
            "Recovers finished job result(s). Use jobId for one job or jobIds for batch. Non-terminal jobs return ready=false.",
            """{"type":"object","properties":{"jobId":{"type":"string"},"jobIds":{"type":"array","items":{"type":"string"}}},"additionalProperties":false}""",
            """{"type":"object","properties":{"success":{"type":"boolean"},"ready":{"type":"boolean"},"jobId":{"type":"string"},"state":{"type":"string"},"result":{"type":"string"},"error":{"type":"string"},"jobs":{"type":"array"},"message":{"type":"string"}},"required":["success","message"],"additionalProperties":false}""",
            RiskLevel: "low",
            Capabilities: ["agent.delegate"],
            Risk: ToolRiskLevel.Low,
            IsIdempotent: true,
            SupportedModes: ["expert"]),
        new(
            AgentJobWaitToolId,
            "mcli",
            "Wait and recover agent jobs",
            "Waits until listed parallel jobs reach a terminal state (or timeout), then recovers their results.",
            """{"type":"object","properties":{"jobId":{"type":"string"},"jobIds":{"type":"array","items":{"type":"string"}},"timeoutMs":{"type":"integer"}},"additionalProperties":false}""",
            """{"type":"object","properties":{"success":{"type":"boolean"},"allTerminal":{"type":"boolean"},"allSucceeded":{"type":"boolean"},"timedOut":{"type":"boolean"},"jobs":{"type":"array"},"message":{"type":"string"}},"required":["success","message"],"additionalProperties":false}""",
            RiskLevel: "low",
            Capabilities: ["agent.delegate"],
            Risk: ToolRiskLevel.Low,
            IsIdempotent: false,
            SupportedModes: ["expert"]),
        new(
            AgentJobCancelToolId,
            "mcli",
            "Cancel agent job",
            "Requests cancellation of a parallel sub-agent job. State becomes cancelling until the worker stops, then cancelled.",
            """{"type":"object","properties":{"jobId":{"type":"string"}},"required":["jobId"],"additionalProperties":false}""",
            """{"type":"object","properties":{"success":{"type":"boolean"},"cancelRequested":{"type":"boolean"},"jobId":{"type":"string"},"state":{"type":"string"},"message":{"type":"string"}},"required":["success","message"],"additionalProperties":false}""",
            RiskLevel: "low",
            Capabilities: ["agent.delegate"],
            Risk: ToolRiskLevel.Low,
            IsIdempotent: false,
            SupportedModes: ["expert"]),
        new(
            MeetingOutputSummaryToolId,
            "mcli",
            "Output meeting summary",
            "Writes the meeting summary as an independent markdown artifact and registers it for end confirmation.",
            """{"type":"object","properties":{"summary":{"type":"string","minLength":1}},"required":["summary"],"additionalProperties":false}""",
            """{"type":"object","properties":{"ok":{"type":"boolean"},"summaryMessageId":{"type":"string"},"summaryPath":{"type":"string"},"next":{"type":"string"}},"required":["ok"],"additionalProperties":false}""",
            RiskLevel: "low",
            Capabilities: ["meeting.summary"],
            Risk: ToolRiskLevel.Low,
            IsIdempotent: false,
            SupportedModes: ["meeting"]),
        new(
            MeetingConfirmEndToolId,
            "mcli",
            "Confirm meeting end",
            "Marks the meeting as explicitly ended after a summary has been produced.",
            """{"type":"object","properties":{},"additionalProperties":false}""",
            """{"type":"object","properties":{"ok":{"type":"boolean"},"endConfirmed":{"type":"boolean"},"finalSummaryMessageId":{"type":"string"}},"required":["ok"],"additionalProperties":false}""",
            RiskLevel: "medium",
            RequiresApproval: false,
            Capabilities: ["meeting.control"],
            Risk: ToolRiskLevel.Write,
            IsIdempotent: false,
            SupportedModes: ["meeting"]),
        new(
            MeetingListAttachmentsToolId,
            "mcli",
            "List meeting attachments",
            "Lists attachment metadata for the current meeting without reading file contents.",
            """{"type":"object","properties":{},"additionalProperties":false}""",
            """{"type":"object","properties":{"ok":{"type":"boolean"},"attachmentRoot":{"type":"string"},"attachments":{"type":"array"},"empty":{"type":"boolean"}},"required":["ok","attachments","empty"],"additionalProperties":false}""",
            RiskLevel: "low",
            Capabilities: ["meeting.attachments"],
            Risk: ToolRiskLevel.SensitiveRead,
            SupportedModes: ["meeting"]),
        new(
            AskUserToolId,
            "mcli",
            "Ask user",
            "Asks the host/user a short question and blocks the current invocation until HITL responds. Does not use the interrupt queue.",
            """{"type":"object","properties":{"question":{"type":"string","minLength":1}},"required":["question"],"additionalProperties":false}""",
            """{"type":"object","properties":{"ok":{"type":"boolean"},"waitingForUser":{"type":"boolean"},"requestId":{"type":"string"},"question":{"type":"string"},"userAnswer":{"type":"string"},"message":{"type":"string"}},"required":["ok"],"additionalProperties":false}""",
            RiskLevel: "low",
            Capabilities: ["meeting.hitl", "work.hitl"],
            Risk: ToolRiskLevel.Low,
            IsIdempotent: false,
            SupportedModes: ["meeting", "work"]),
        new(
            CheckPendingUserInputToolId,
            "mcli",
            "Check pending user input",
            "Peeks whether the current session has unconsumed user interrupt input from meeting.input.enqueue.",
            """{"type":"object","properties":{},"additionalProperties":false}""",
            """{"type":"object","properties":{"ok":{"type":"boolean"},"hasPending":{"type":"boolean"},"pending":{"type":"array"},"message":{"type":"string"}},"required":["ok","hasPending","pending","message"],"additionalProperties":false}""",
            RiskLevel: "low",
            Capabilities: ["meeting.hitl", "work.hitl"],
            Risk: ToolRiskLevel.Low,
            SupportedModes: ["meeting", "work"]),
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
            SupportedModes: ["work"]),
        new(
            ExpertCreatePlanToolId,
            "mcli",
            "Create execution plan",
            "Create a step-by-step execution plan for medium-complexity tasks. Use this when the task requires multiple ordered steps. Returns planId for tracking.",
            """{"type":"object","properties":{"title":{"type":"string","description":"Concise plan title"},"steps":{"type":"array","items":{"type":"object","properties":{"title":{"type":"string","description":"Step title"},"description":{"type":"string","description":"Detailed step description"},"estimatedMinutes":{"type":"integer","minimum":1},"dependencies":{"type":"array","items":{"type":"integer"}}},"required":["title","description"],"additionalProperties":false}}},"required":["title","steps"],"additionalProperties":false}""",
            """{"type":"object","properties":{"success":{"type":"boolean"},"planId":{"type":"string"},"title":{"type":"string"},"stepCount":{"type":"integer"},"message":{"type":"string"}},"required":["success","message"],"additionalProperties":false}""",
            RiskLevel: "low",
            Capabilities: ["expert.plan"],
            Risk: ToolRiskLevel.Low,
            IsIdempotent: false,
            SupportedModes: new[] { "expert" }),
        new(
            ExpertExecuteStepToolId,
            "mcli",
            "Execute plan step",
            "Execute a single step from the active plan. Marks the step as in-progress, performs the work, then marks it completed or failed. Returns execution result.",
            """{"type":"object","properties":{"stepIndex":{"type":"integer","minimum":0,"description":"Zero-based step index"},"notes":{"type":"string","description":"Optional execution notes"}},"required":["stepIndex"],"additionalProperties":false}""",
            """{"type":"object","properties":{"success":{"type":"boolean"},"stepIndex":{"type":"integer"},"stepTitle":{"type":"string"},"status":{"type":"string"},"result":{"type":"string"},"message":{"type":"string"}},"required":["success","message"],"additionalProperties":false}""",
            RiskLevel: "low",
            Capabilities: ["expert.plan"],
            Risk: ToolRiskLevel.Low,
            IsIdempotent: false,
            SupportedModes: new[] { "expert" }),
        new(
            ExpertGetPlanToolId,
            "mcli",
            "Get active plan",
            "Retrieves the currently active execution plan with all steps and their current status. Use this to check plan progress.",
            """{"type":"object","properties":{},"additionalProperties":false}""",
            """{"type":"object","properties":{"success":{"type":"boolean"},"hasPlan":{"type":"boolean"},"planId":{"type":"string"},"title":{"type":"string"},"status":{"type":"string"},"steps":{"type":"array","items":{"type":"object"}},"message":{"type":"string"}},"required":["success","hasPlan","message"],"additionalProperties":false}""",
            RiskLevel: "low",
            Capabilities: ["expert.plan"],
            Risk: ToolRiskLevel.Low,
            IsIdempotent: true,
            SupportedModes: new[] { "expert" }),
        new(
            ExpertUpdateStepStatusToolId,
            "mcli",
            "Update step status",
            "Manually update the status of a specific plan step. Useful for marking steps as skipped, failed, or completed without execution.",
            """{"type":"object","properties":{"stepIndex":{"type":"integer","minimum":0,"description":"Zero-based step index"},"newStatus":{"type":"string","enum":["pending","running","completed","failed","skipped"],"description":"New step status"},"notes":{"type":"string","description":"Optional reason for status change"}},"required":["stepIndex","newStatus"],"additionalProperties":false}""",
            """{"type":"object","properties":{"success":{"type":"boolean"},"stepIndex":{"type":"integer"},"oldStatus":{"type":"string"},"newStatus":{"type":"string"},"message":{"type":"string"}},"required":["success","message"],"additionalProperties":false}""",
            RiskLevel: "low",
            Capabilities: ["expert.plan"],
            Risk: ToolRiskLevel.Low,
            IsIdempotent: false,
            SupportedModes: new[] { "expert" }),
        new(
            ExpertAbandonPlanToolId,
            "mcli",
            "Abandon plan",
            "Abandons the currently active plan and marks it as cancelled. Use this when the plan is no longer needed or cannot be completed.",
            """{"type":"object","properties":{"reason":{"type":"string","description":"Optional reason for abandoning the plan"}},"additionalProperties":false}""",
            """{"type":"object","properties":{"success":{"type":"boolean"},"planId":{"type":"string"},"message":{"type":"string"}},"required":["success","message"],"additionalProperties":false}""",
            RiskLevel: "low",
            Capabilities: ["expert.plan"],
            Risk: ToolRiskLevel.Low,
            IsIdempotent: false,
            SupportedModes: new[] { "expert" })
    ]);

    private static readonly IReadOnlyList<ToolDescriptor> Tools = Array.AsReadOnly(
        [.. AdvertisedTools, .. BuiltinToolAliasCatalog.Tools]);

    public string CatalogVersion => "3.4.0";

    public IReadOnlyList<ToolDescriptor> GetTools() => Tools;

    public IReadOnlyList<ToolDescriptor> GetTools(string[] toolIds)
    {
        ArgumentNullException.ThrowIfNull(toolIds);
        var requested = toolIds.ToHashSet(StringComparer.Ordinal);
        return Tools.Where(tool => requested.Contains(tool.ToolId)).ToArray();
    }
}
