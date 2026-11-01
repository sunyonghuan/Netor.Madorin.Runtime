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
    public const string FileAttachToolId = "mcli_fs_attach";
    public const string FileWriteToolId = "mcli_fs_write";
    public const string FileAppendToolId = "mcli_fs_append";
    public const string DirectoryCreateToolId = "mcli_fs_mkdir";
    public const string FileCopyToolId = "mcli_fs_copy";
    public const string FileMoveToolId = "mcli_fs_move";
    public const string FileDeleteToolId = "mcli_fs_delete";
    public const string FileSearchToolId = "mcli_fs_search";
    public const string FileEditToolId = "mcli_fs_edit";
    public const string CommandToolId = CommandToolAuthorization.ToolId;
    public const string HttpRequestToolId = "mcli_http";
    public const string DelegateToAgentToolId = "mcli_delegate_agent";
    public const string AgentJobCreateToolId = "mcli_agent_job_create";
    public const string AgentJobCreateManyToolId = "mcli_agent_job_create_many";
    public const string AgentJobStatusToolId = "mcli_agent_job_status";
    public const string AgentJobResultToolId = "mcli_agent_job_result";
    public const string AgentJobWaitToolId = "mcli_agent_job_wait";
    public const string AgentJobCancelToolId = "mcli_agent_job_cancel";
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
            FileAttachToolId,
            "mcli",
            "Attach file",
            "Reads one authorized local file as an attachment. Returns bounded metadata and a separate Blob, without decoding the file as text.",
            """{"type":"object","properties":{"path":{"type":"string"},"expectedHash":{"type":"string"}},"required":["path"],"additionalProperties":false}""",
            """{"type":"object","properties":{"fileName":{"type":"string"},"contentType":{"type":"string"},"byteLength":{"type":"integer"},"sha256":{"type":"string"}},"required":["fileName","contentType","byteLength","sha256"],"additionalProperties":false}""",
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
            "Searches text under path and returns one page of the best matching files, ranked by how often query appears. page starts at 1. pageSize or maxResults sets the page length from 1 to 100; the default is 20. The result has matches, total, page, pageSize, and hasMore. Request the next page only when hasMore is true.",
            """{"type":"object","properties":{"path":{"type":"string"},"query":{"type":"string"},"encoding":{"type":"string","enum":["auto","utf-8","utf-16le","utf-16be","gb2312","gbk","gb18030","ascii","latin1"]},"pattern":{"type":"string"},"caseSensitive":{"type":"boolean"},"page":{"type":"integer","minimum":1},"pageSize":{"type":"integer","minimum":1,"maximum":100},"maxResults":{"type":"integer","minimum":1,"maximum":100}},"required":["path","query","encoding"],"additionalProperties":false}""",
            """{"type":"object","properties":{"matches":{"type":"array"},"total":{"type":"integer"},"page":{"type":"integer"},"pageSize":{"type":"integer"},"hasMore":{"type":"boolean"}},"required":["matches","total","page","pageSize","hasMore"],"additionalProperties":false}""",
            Capabilities: ["filesystem.read"],
            Risk: ToolRiskLevel.SensitiveRead),
        new(
            FileEditToolId,
            "mcli",
            "Edit file",
            "Applies 1 to 100 line edits. Read the file first and pass its sha256 as expectedHash. Each operations item has type: replace, delete, insertBefore, or insertAfter. replace and delete require startLine and endLine (1-based, inclusive). replace also requires content. insertBefore and insertAfter require line and content.",
            """{"type":"object","properties":{"path":{"type":"string"},"encoding":{"type":"string","enum":["utf-8","utf-16le","utf-16be","gb2312","gbk","gb18030","ascii","latin1"]},"expectedHash":{"type":"string","description":"SHA-256 hex from mcli_fs_read or mcli_fs_inspect."},"operations":{"type":"array","minItems":1,"maxItems":100,"items":{"type":"object","additionalProperties":false,"required":["type"],"properties":{"type":{"type":"string","enum":["replace","delete","insertBefore","insertAfter"]},"startLine":{"type":"integer","minimum":1,"description":"Required for replace and delete. Inclusive, 1-based."},"endLine":{"type":"integer","minimum":1,"description":"Required for replace and delete. Inclusive, 1-based."},"line":{"type":"integer","minimum":1,"description":"Required for insertBefore and insertAfter. 1-based."},"content":{"type":"string","description":"Required for replace, insertBefore, and insertAfter."}}}}},"required":["path","encoding","expectedHash","operations"],"additionalProperties":false}""",
            """{"type":"object","properties":{"edited":{"type":"boolean"},"byteLength":{"type":"integer"},"sha256":{"type":"string"}},"required":["edited","byteLength","sha256"],"additionalProperties":false}""",
            Capabilities: ["filesystem.read", "filesystem.write"],
            Risk: ToolRiskLevel.Write,
            IsIdempotent: false),
        new(
            CommandToolId,
            "mcli",
            "Run command",
            "Run commands and programs such as git, dotnet, Python, and installed CLIs. Put the command and its arguments in script; separate multiple commands with newlines to execute them in one shell process. Use the current platform's shell syntax and quote paths containing spaces. Script text, paths, and arguments use Unicode. Keep script within 8 KiB UTF-8: use mcli_fs_write for long prompts, request bodies, or scripts, then pass file paths using the program's supported file-input options or invoke script files with their interpreter. Do not read a long file back into a command-line argument. Alternatively, use stdin (up to 1 MiB UTF-8) when the program supports standard input. Input errors are returned as tool failures; correct the input and retry with a new tool call id. Permission denials must not be bypassed. Returns shell, exitCode, stdout, stderr, stdoutTruncated, and stderrTruncated. Example: {\"script\":\"git status\\ngit diff\"}.",
            """
            {
              "type":"object",
              "properties":{
                "script":{"type":"string","minLength":1,"description":"Required command or multiline script, including program arguments. Maximum 8 KiB of UTF-8 script text."},
                "shell":{"type":"string","enum":["auto","pwsh","powershell","cmd","bash","zsh","sh"],"default":"auto","description":"Script interpreter. Default auto: Windows selects pwsh, then powershell, then cmd; macOS/Linux selects bash, then zsh, then sh. Commands and programs belong in script."},
                "workingDirectory":{"type":"string","description":"Initial working directory. Default: current workspace root. Relative paths resolve from the workspace root."},
                "environment":{"type":"object","additionalProperties":{"type":"string"},"default":{},"description":"Environment variable overrides, as string values. Default: inherit the full CLI environment."},
                "stdin":{"type":"string","default":"","description":"Standard input text, up to 1 MiB UTF-8. Use for long prompts or request bodies only when the program supports standard input. Default: empty input followed by EOF."},
                "encoding":{"type":"string","enum":["utf-8","gbk","gb18030"],"default":"utf-8","description":"Character encoding for stdin, stdout, and stderr. Default utf-8. Select gbk or gb18030 for programs using that stream encoding."}
              },
              "required":["script"],
              "additionalProperties":false
            }
            """,
            """{"type":"object","properties":{"exitCode":{"type":"integer"},"stdout":{"type":"string"},"stderr":{"type":"string"},"stdoutTruncated":{"type":"boolean"},"stderrTruncated":{"type":"boolean"},"shell":{"type":"string"}},"required":["exitCode","stdout","stderr","stdoutTruncated","stderrTruncated","shell"],"additionalProperties":false}""",
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
            """{"type":"object","properties":{"agentName":{"type":"string","description":"Name or capability of the target agent"},"task":{"type":"string","description":"Task description to delegate"},"createIfMissing":{"type":"boolean","description":"Create a one-shot temporary agent when no existing agent matches"},"temporary":{"type":"boolean","description":"The created agent is ephemeral and is destroyed after the task"},"role":{"type":"string"},"capabilities":{"type":"array","items":{"type":"string"}},"allowedToolIds":{"type":"array","items":{"type":"string"}},"timeoutMs":{"type":"integer","minimum":1000}},"required":["agentName","task"],"additionalProperties":false}""",
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
            """{"type":"object","properties":{"agentName":{"type":"string"},"task":{"type":"string"},"timeoutMs":{"type":"integer","minimum":1000},"createIfMissing":{"type":"boolean"},"temporary":{"type":"boolean"},"role":{"type":"string"},"capabilities":{"type":"array","items":{"type":"string"}},"allowedToolIds":{"type":"array","items":{"type":"string"}}},"required":["agentName","task"],"additionalProperties":false}""",
            """{"type":"object","properties":{"success":{"type":"boolean"},"jobId":{"type":"string"},"state":{"type":"string"},"agentName":{"type":"string"},"task":{"type":"string"},"startedAt":{"type":"string"},"completedAt":{"type":"string"},"error":{"type":"string"},"terminal":{"type":"boolean"},"message":{"type":"string"}},"required":["success","message"],"additionalProperties":false}""",
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
            """{"type":"object","properties":{"jobs":{"type":"array","items":{"type":"object","properties":{"agentName":{"type":"string"},"task":{"type":"string"},"timeoutMs":{"type":"integer","minimum":1000},"createIfMissing":{"type":"boolean"},"temporary":{"type":"boolean"},"role":{"type":"string"},"capabilities":{"type":"array","items":{"type":"string"}},"allowedToolIds":{"type":"array","items":{"type":"string"}}},"required":["agentName","task"],"additionalProperties":false}}},"required":["jobs"],"additionalProperties":false}""",
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
            """{"type":"object","properties":{"success":{"type":"boolean"},"jobId":{"type":"string"},"state":{"type":"string"},"agentName":{"type":"string"},"task":{"type":"string"},"startedAt":{"type":"string"},"completedAt":{"type":"string"},"error":{"type":"string"},"terminal":{"type":"boolean"},"message":{"type":"string"}},"required":["success","message"],"additionalProperties":false}""",
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
            """{"type":"object","properties":{"success":{"type":"boolean"},"ready":{"type":"boolean"},"allReady":{"type":"boolean"},"allSucceeded":{"type":"boolean"},"agentName":{"type":"string"},"task":{"type":"string"},"jobId":{"type":"string"},"state":{"type":"string"},"result":{"type":"string"},"error":{"type":"string"},"jobs":{"type":"array"},"message":{"type":"string"}},"required":["success","message"],"additionalProperties":false}""",
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
            ExpertCreatePlanToolId,
            "mcli",
            "Create execution plan",
            "Create a step-by-step execution plan for medium-complexity tasks. Use this when the task requires multiple ordered steps. Each step may name an available sub-agent in agentName.",
            """{"type":"object","properties":{"goal":{"type":"string","description":"Plan goal"},"steps":{"type":"array","items":{"type":"object","properties":{"title":{"type":"string","description":"Step title"},"description":{"type":"string","description":"Detailed step description"},"agentName":{"type":"string","description":"Optional available sub-agent responsible for this step"},"executionPolicy":{"type":"string","enum":["PrimaryAgent","ExistingAgentOnly","TemporaryAgentIfUnmatched","PreferParallelTemporaryAgents"]},"parallelTasks":{"type":"array","minItems":2,"maxItems":5,"items":{"type":"string"},"description":"Required independent subtasks for PreferParallelTemporaryAgents"},"completionMode":{"type":"string","enum":["tools","analysis"],"description":"analysis is for text-only reasoning, requires expectedOutput"},"expectedOutput":{"type":"string","description":"Exact text required in an analysis step result"}},"required":["title"],"additionalProperties":false}}},"required":["goal","steps"],"additionalProperties":false}""",
            """{"type":"object","properties":{"success":{"type":"boolean"},"planId":{"type":"string"},"goal":{"type":"string"},"totalSteps":{"type":"integer"},"steps":{"type":"array"}},"required":["success","planId","goal","totalSteps","steps"],"additionalProperties":false}""",
            RiskLevel: "low",
            Capabilities: ["expert.plan"],
            Risk: ToolRiskLevel.Low,
            IsIdempotent: false,
            SupportedModes: new[] { "expert" }),
        new(
            ExpertExecuteStepToolId,
            "mcli",
            "Execute plan step",
            "Execute the zero-based stepIndex and complete it only after runtime-verified work. Assigned Agent policies run during this call.",
            """{"type":"object","properties":{"stepIndex":{"type":"integer","minimum":0,"description":"Zero-based index of a step in the active plan"},"notes":{"type":"string","description":"Optional execution notes"},"result":{"type":"string","description":"Required exact result when completionMode=analysis"}},"required":["stepIndex"],"additionalProperties":false}""",
            """{"type":"object","properties":{"success":{"type":"boolean"},"stepIndex":{"type":"integer"},"message":{"type":"string"}},"required":["success","stepIndex","message"],"additionalProperties":false}""",
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
            """{"type":"object","properties":{"success":{"type":"boolean"},"hasPlan":{"type":"boolean"},"planId":{"type":"string"},"title":{"type":"string"},"status":{"type":"string"},"steps":{"type":"array","items":{"type":"object","properties":{"index":{"type":"integer"},"stepId":{"type":"string"},"title":{"type":"string"},"description":{"type":"string"},"status":{"type":"string"},"progress":{"type":"integer"},"agentName":{"type":"string"},"result":{"type":"string"},"error":{"type":"string"}},"required":["index","stepId","title","status","progress"],"additionalProperties":false}},"message":{"type":"string"}},"required":["success","hasPlan","message"],"additionalProperties":false}""",
            RiskLevel: "low",
            Capabilities: ["expert.plan"],
            Risk: ToolRiskLevel.Low,
            IsIdempotent: true,
            SupportedModes: new[] { "expert" }),
        new(
            ExpertUpdateStepStatusToolId,
            "mcli",
            "Update step status",
            "Manually update a plan step status by zero-based stepIndex. Use this to mark a step running before work or failed/skipped when applicable.",
            """{"type":"object","properties":{"stepIndex":{"type":"integer","minimum":0},"newStatus":{"type":"string","enum":["pending","running","failed","skipped"],"description":"New step status"},"notes":{"type":"string","description":"Optional reason for status change"}},"required":["stepIndex","newStatus"],"additionalProperties":false}""", 
            """{"type":"object","properties":{"success":{"type":"boolean"},"stepIndex":{"type":"integer"},"oldStatus":{"type":"string"},"newStatus":{"type":"string"},"message":{"type":"string"}},"required":["success","stepIndex","oldStatus","newStatus","message"],"additionalProperties":false}""",
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
            """{"type":"object","properties":{"success":{"type":"boolean"},"planId":{"type":"string"},"message":{"type":"string"}},"required":["success","planId","message"],"additionalProperties":false}""",
            RiskLevel: "low",
            Capabilities: ["expert.plan"],
            Risk: ToolRiskLevel.Low,
            IsIdempotent: false,
            SupportedModes: new[] { "expert" })
    ]);

    private static readonly IReadOnlyList<ToolDescriptor> Tools = Array.AsReadOnly(
        [.. AdvertisedTools, .. BuiltinToolAliasCatalog.Tools]);

    public string CatalogVersion => "3.5.3";

    public IReadOnlyList<ToolDescriptor> GetTools() => Tools;

    public IReadOnlyList<ToolDescriptor> GetTools(string[] toolIds)
    {
        ArgumentNullException.ThrowIfNull(toolIds);
        var requested = toolIds.ToHashSet(StringComparer.Ordinal);
        return Tools.Where(tool => requested.Contains(tool.ToolId)).ToArray();
    }
}
