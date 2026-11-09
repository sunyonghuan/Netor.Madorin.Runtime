using System.Collections.Concurrent;
using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;

const string memoryToken = "青竹915";
const string fileToken = "HOST-FILE-TOKEN-8821";
const string skillPassphrase = "HOST-SKILL-ORANGE-7719";
const string writeMarker = "HOST-WRITE-OK";

var repoRoot = SmokeSupport.FindRepoRoot();
var runtimePath = SmokeSupport.ResolvePublishedRuntime(repoRoot);
var (provider, modelId) = SmokeSupport.LoadProviderFromGlobalConfig();
var outputRoot = Path.Combine(repoRoot, "artifacts", "host-expert-smoke-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
var workspace = Path.Combine(outputRoot, "workspace");
var dataDir = Path.Combine(outputRoot, "data");
Directory.CreateDirectory(workspace);
Directory.CreateDirectory(dataDir);
Directory.CreateDirectory(Path.Combine(workspace, ".madorin", "skills", "echo-token"));
await File.WriteAllTextAsync(Path.Combine(workspace, "note.txt"), fileToken + Environment.NewLine);
await File.WriteAllTextAsync(
    Path.Combine(workspace, ".madorin", "skills", "echo-token", "SKILL.md"),
    """
    ---
    name: echo-token
    description: Host expert smoke skill that reveals a unique passphrase after it is loaded.
    ---

    # Echo Token

    After this skill is loaded, reply with this passphrase exactly: HOST-SKILL-ORANGE-7719
    """);

var policy = new PermissionPolicy();
var permissionLog = new ConcurrentBag<string>();
var callbacks = new RuntimeHostCallbacks
{
    CallbackTimeout = TimeSpan.FromSeconds(45),
    ToolPermissionRequestedAsync = (request, ct) =>
    {
        ct.ThrowIfCancellationRequested();
        var deny = policy.ShouldDeny(request.ToolId);
        permissionLog.Add($"{request.ToolId}:{(deny ? "Denied" : "Granted")}");
        return ValueTask.FromResult(deny
            ? new ToolPermissionResponse(
                request.CorrelationId ?? request.ApprovalRequestId,
                request.CallId,
                ToolAuthorizationDecision.Denied,
                Reason: "host expert smoke deny")
            : new ToolPermissionResponse(
                request.CorrelationId ?? request.ApprovalRequestId,
                request.CallId,
                ToolAuthorizationDecision.Granted,
                SmokeSupport.CreateGrant(workspace, request),
                "host expert smoke grant"));
    },
    ApprovalRequestedAsync = (request, ct) =>
    {
        ct.ThrowIfCancellationRequested();
        var deny = policy.ShouldDeny(request.ToolId);
        permissionLog.Add($"{request.ToolId}:approval:{(deny ? "Denied" : "Granted")}");
        return ValueTask.FromResult(deny
            ? new ApprovalResponse(
                request.CorrelationId,
                request.ApprovalRequestId,
                ToolAuthorizationDecision.Denied,
                Reason: "host expert smoke deny")
            : new ApprovalResponse(
                request.CorrelationId,
                request.ApprovalRequestId,
                ToolAuthorizationDecision.Granted,
                SmokeTurns.CreateGrantFromApproval(workspace, request),
                "host expert smoke grant"));
    }
};

Console.WriteLine($"RUNTIME|{runtimePath}");
Console.WriteLine($"PROVIDER|{provider.ProviderId}|model={modelId}|protocol={provider.Protocol}|baseUrl={provider.BaseUrl}");
Console.WriteLine($"WORKSPACE|{workspace}");

var cases = new List<CaseResult>();
await using var client = await RuntimeClient.CreateAsync(
    new RuntimeClientOptions(
        PipeName: string.Empty,
        HostInstanceId: Guid.NewGuid().ToString("N"),
        RuntimeExecutablePath: runtimePath,
        WorkspaceDirectory: workspace,
        DataDirectory: dataDir,
        LogDirectory: Path.Combine(dataDir, "logs"),
        StartPolicy: RuntimeStartPolicy.AlwaysStart,
        StartupTimeout: TimeSpan.FromSeconds(90),
        ReconnectWindow: TimeSpan.FromSeconds(30),
        Capabilities: new RuntimeCapabilities(
            ReverseRpc: true,
            ToolCatalog: true,
            ToolPermissions: true,
            Skills: new SkillsCapabilities(Enabled: true, DiscoveryDirect: true)),
        HostCallbacks: callbacks));
cases.Add(new CaseResult(
    "connect",
    true,
    $"runtime={client.InstanceBinding.RuntimeInstanceId}; pid={client.InstanceBinding.RuntimeProcessId}"));
await client.UpsertProvidersAsync([provider]);
var catalog = await client.GetSkillCatalogAsync();
var skillFound = catalog.Skills.Any(static skill => skill.Name == "echo-token");
cases.Add(new CaseResult(
    "skill-catalog",
    skillFound,
    $"count={catalog.SkillCount}; names={string.Join(',', catalog.Skills.Select(static skill => skill.Name))}"));

var replySelection = SmokeSupport.Select(
    provider.ProviderId,
    modelId,
    "你是简短助手。禁止调用工具。只按用户要求回复，不要解释。",
    skillIds: null,
    allowedTools: []);
var toolSelection = SmokeSupport.Select(
    provider.ProviderId,
    modelId,
    "你是工具测试助手。必须按用户指定调用工具，禁止假装已执行。拿到真实工具结果后用一句中文或令牌回复。",
    skillIds: null,
    allowedTools: ["mcli_fs_read", "mcli_fs_write", "mcli_fs_list", "mcli_command"]);
var skillSelection = SmokeSupport.Select(
    provider.ProviderId,
    modelId,
    "你是技能测试助手。必须调用 load_skill / runtime.skill.read，参数 name=echo-token。然后只回复技能正文中的口令。",
    skillIds: ["echo-token"],
    allowedTools: ["runtime.skill.read", "runtime.skill.load", "runtime.skill.read_resource"]);

long cursor = 0;
policy.Mode = PermissionMode.Grant;
string? replySession = null;
string[] replyPrompts =
[
    $"请记住口令：{memoryToken}。只回复这个口令，不要调用工具。",
    "我刚才让你记住的口令是什么？只回复口令。",
    "把口令后面加上-OK，只回复结果。"
];
string[] replyExpected = [memoryToken, memoryToken, memoryToken + "-OK"];
for (var i = 0; i < replyPrompts.Length; i++)
{
    var turn = await SmokeTurns.CaptureAsync(
        client, provider, replySelection, replyPrompts[i], replySession, cursor, TimeSpan.FromMinutes(3));
    cursor = Math.Max(cursor, turn.LastGsn);
    replySession = string.IsNullOrWhiteSpace(turn.SessionId) ? replySession : turn.SessionId;
    cases.Add(new CaseResult(
        $"reply-turn-{i + 1}",
        turn.Terminal == MessageTypes.RunCompleted && turn.Text.Contains(replyExpected[i], StringComparison.Ordinal),
        SmokeTurns.Preview(turn)));
}

var readTurn = await SmokeTurns.CaptureAsync(
    client,
    provider,
    toolSelection,
    "请调用 mcli_fs_read 读取 note.txt，并根据真实文件内容回复其中的令牌。不要编造。",
    null,
    cursor,
    TimeSpan.FromMinutes(4));
cursor = Math.Max(cursor, readTurn.LastGsn);
cases.Add(new CaseResult(
    "tool-read",
    readTurn.Terminal == MessageTypes.RunCompleted
        && readTurn.ToolIds.Contains("mcli_fs_read")
        && readTurn.Text.Contains(fileToken, StringComparison.Ordinal),
    SmokeTurns.Preview(readTurn)));

var writeTurn = await SmokeTurns.CaptureAsync(
    client,
    provider,
    toolSelection,
    $"请调用 mcli_fs_write 把文本 {writeMarker} 写入 written.txt。写入成功后只回复 {writeMarker}。",
    string.IsNullOrWhiteSpace(readTurn.SessionId) ? null : readTurn.SessionId,
    cursor,
    TimeSpan.FromMinutes(4));
cursor = Math.Max(cursor, writeTurn.LastGsn);
var writtenPath = Path.Combine(workspace, "written.txt");
var fileOk = File.Exists(writtenPath)
    && (await File.ReadAllTextAsync(writtenPath)).Contains(writeMarker, StringComparison.Ordinal);
var writeGranted = permissionLog.Any(static item =>
    item.StartsWith("mcli_fs_write:Granted", StringComparison.Ordinal));
cases.Add(new CaseResult(
    "tool-authorize-write",
    writeTurn.Terminal == MessageTypes.RunCompleted
        && writeTurn.ToolIds.Contains("mcli_fs_write")
        && fileOk
        && writeGranted,
    SmokeTurns.Preview(writeTurn) + $"; fileOk={fileOk}; granted={writeGranted}"));

var skillTurn = await SmokeTurns.CaptureAsync(
    client,
    provider,
    skillSelection,
    "请立刻调用 load_skill（工具 runtime.skill.read）加载技能 echo-token，然后只回复技能正文中的口令。",
    null,
    cursor,
    TimeSpan.FromMinutes(4));
cursor = Math.Max(cursor, skillTurn.LastGsn);
var skillTool = skillTurn.ToolIds.Any(static id => id.Contains("skill", StringComparison.OrdinalIgnoreCase));
cases.Add(new CaseResult(
    "skill-load",
    skillTurn.Terminal == MessageTypes.RunCompleted
        && skillTool
        && skillTurn.Text.Contains(skillPassphrase, StringComparison.Ordinal),
    SmokeTurns.Preview(skillTurn)));

policy.Mode = PermissionMode.DenyWrite;
var denyTurn = await SmokeTurns.CaptureAsync(
    client,
    provider,
    toolSelection,
    "请调用 mcli_fs_write 把文本 SHOULD-NOT-EXIST 写入 forbidden.txt。如果授权被拒绝，只回复 DENIED。",
    null,
    cursor,
    TimeSpan.FromMinutes(4));
var forbiddenMissing = !File.Exists(Path.Combine(workspace, "forbidden.txt"));
var writeDenied = permissionLog.Any(static item =>
    item.Contains("mcli_fs_write", StringComparison.Ordinal)
    && item.Contains("Denied", StringComparison.Ordinal));
var writeFailed = denyTurn.FailedTools.Contains("mcli_fs_write")
    || denyTurn.Text.Contains("DENIED", StringComparison.OrdinalIgnoreCase);
cases.Add(new CaseResult(
    "tool-deny-write",
    denyTurn.Terminal == MessageTypes.RunCompleted
        && forbiddenMissing
        && writeDenied,
    SmokeTurns.Preview(denyTurn)
        + $"; fileMissing={forbiddenMissing}; denied={writeDenied}; failedTool={writeFailed}"));

foreach (var item in cases)
{
    Console.WriteLine($"CASE|{item.Name}|ok={item.Ok}|{item.Detail}");
}

var passed = cases.TrueForAll(static item => item.Ok);
var report = BuildReport(runtimePath, provider.ProviderId, modelId, provider.Protocol, provider.BaseUrl, outputRoot, cases);
var reportPath = Path.Combine(outputRoot, "report.md");
await File.WriteAllTextAsync(reportPath, report);
Directory.CreateDirectory(Path.Combine(repoRoot, "docs", "测试"));
await File.WriteAllTextAsync(
    Path.Combine(repoRoot, "docs", "测试", "2026-09-18-专家模式宿主发布产物测试.md"),
    report);
Console.WriteLine($"REPORT|{reportPath}");
Console.WriteLine(passed ? "RESULT|PASS" : "RESULT|FAIL");
return passed ? 0 : 1;

static string BuildReport(
    string runtimePath,
    string providerId,
    string modelId,
    string protocol,
    string baseUrl,
    string outputRoot,
    List<CaseResult> cases)
{
    var passed = cases.FindAll(static item => item.Ok).Count;
    var lines = new List<string>
    {
        "# 专家模式宿主发布产物测试报表",
        string.Empty,
        $"测试时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}（本地）",
        "模式：Expert；通道：宿主 SDK 拉起发布产物（RuntimeStartPolicy.AlwaysStart）",
        $"Runtime：`{runtimePath}`",
        $"Provider：{providerId} / {modelId}（{protocol} {baseUrl}）",
        $"产物目录：`{outputRoot}`",
        $"结果：{passed}/{cases.Count} 通过",
        string.Empty,
        "| 用例 | 结果 | 细节 |",
        "| --- | --- | --- |"
    };
    foreach (var item in cases)
    {
        lines.Add($"| {item.Name} | {(item.Ok ? "通过" : "失败")} | {item.Detail.Replace("|", "/", StringComparison.Ordinal)} |");
    }

    return string.Join('\n', lines) + '\n';
}
