namespace Madorin.AI.Runtime.MConsole.Menu;

public sealed class CommandCatalog
{
    private readonly IReadOnlyDictionary<string, CommandDefinition> _commands;

    public CommandCatalog(IReadOnlyList<CommandGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        Groups = groups;
        var commands = groups.SelectMany(static group => group.Items).ToArray();
        var duplicate = commands
            .GroupBy(static command => command.Id, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Duplicate command id '{duplicate.Key}'.", nameof(groups));
        }

        _commands = commands.ToDictionary(static command => command.Id, StringComparer.Ordinal);
    }

    public IReadOnlyList<CommandGroup> Groups { get; }

    public IReadOnlyList<CommandDefinition> Commands => _commands.Values
        .OrderBy(static command => command.Id, StringComparer.Ordinal)
        .ToArray();

    public CommandDefinition Get(string id) =>
        _commands.TryGetValue(id, out var command)
            ? command
            : throw new KeyNotFoundException($"Command '{id}' is not registered.");

    public static CommandCatalog CreateDefault() => new(
    [
        new CommandGroup("service", "服务与执行", "Run、REPL 与持久 Runtime。",
        [
            Command("run", "运行任务或进入 REPL", ["run"], CommandRiskLevel.Caution,
                CommandReadWriteKind.WritesRuntimeState, CommandLockKind.WorkspaceWrite,
                [Text("--input", "单次输入"), Path("--input-file", "输入文件"),
                 Path("--output-file", "输出文件"), Enum("--output-format", "输出格式", ["text", "json", "jsonl"]),
                 Flag("--no-stream", "禁用流式输出")], isRepl: true),
            Command("serve", "启动 Runtime serve", ["serve"], CommandRiskLevel.Caution,
                CommandReadWriteKind.ProcessLifecycle, CommandLockKind.WorkspaceWrite,
                [Text("--instance", "实例 ID"), Text("--pipe-prefix", "管道前缀"),
                 Number("--max-runs", "最大并发 Run", 1, 1024)], requiresConfirmation: true),
        ]),
        new CommandGroup("diagnostics", "诊断与信息", "版本和环境诊断。",
        [
            Command("version", "显示版本", ["version"], CommandRiskLevel.Safe,
                CommandReadWriteKind.ReadOnly, CommandLockKind.None),
            Command("doctor", "运行诊断", ["doctor"], CommandRiskLevel.Safe,
                CommandReadWriteKind.ReadOnly, CommandLockKind.WorkspaceRead,
                [Flag("--fix", "修复问题"), Flag("--yes", "确认修复")]),
        ]),
        new CommandGroup("configuration", "配置与 Agent", "独立配置和 Agent 定义。",
        [
            Command("config.init", "初始化配置", ["config", "init"], CommandRiskLevel.Caution, CommandReadWriteKind.WritesConfiguration, CommandLockKind.WorkspaceWrite),
            Command("config.edit", "编辑配置", ["config", "edit"], CommandRiskLevel.Caution, CommandReadWriteKind.WritesConfiguration, CommandLockKind.WorkspaceWrite),
            Command("config.show", "显示配置", ["config", "show"], CommandRiskLevel.Safe, CommandReadWriteKind.ReadOnly, CommandLockKind.WorkspaceRead),
            Command("config.validate", "验证配置", ["config", "validate"], CommandRiskLevel.Safe, CommandReadWriteKind.ReadOnly, CommandLockKind.WorkspaceRead),
            Command("agent.list", "列出 Agents", ["agent", "list"], CommandRiskLevel.Safe, CommandReadWriteKind.ReadOnly, CommandLockKind.None),
            Command("agent.create", "创建 Agent", ["agent", "create"], CommandRiskLevel.Caution, CommandReadWriteKind.WritesConfiguration, CommandLockKind.WorkspaceWrite, [Position("agent-id", "Agent ID")]),
            Command("agent.edit", "编辑 Agent", ["agent", "edit"], CommandRiskLevel.Caution, CommandReadWriteKind.WritesConfiguration, CommandLockKind.WorkspaceWrite, [Position("agent-id", "Agent ID")]),
            Command("agent.delete", "删除 Agent", ["agent", "delete"], CommandRiskLevel.Dangerous, CommandReadWriteKind.WritesConfiguration, CommandLockKind.WorkspaceWrite, [Position("agent-id", "Agent ID")], requiresConfirmation: true),
        ]),
        new CommandGroup("memory-session", "记忆与会话", "Memory 与 Session 生命周期。",
        [
            Command("memory.init", "初始化 Memory", ["memory", "init"], CommandRiskLevel.Caution, CommandReadWriteKind.WritesWorkspace, CommandLockKind.WorkspaceWrite, [Scope()]),
            Command("memory.show", "显示 Memory", ["memory", "show"], CommandRiskLevel.Safe, CommandReadWriteKind.ReadOnly, CommandLockKind.WorkspaceRead, [Scope()]),
            Command("memory.add", "添加 Memory", ["memory", "add"], CommandRiskLevel.Caution, CommandReadWriteKind.WritesWorkspace, CommandLockKind.WorkspaceWrite, [Scope(), Stdin("stdin", "记忆内容")]),
            Command("memory.edit", "编辑 Memory", ["memory", "edit"], CommandRiskLevel.Caution, CommandReadWriteKind.WritesWorkspace, CommandLockKind.WorkspaceWrite, [Scope()]),
            Command("memory.clear", "清空 Memory", ["memory", "clear"], CommandRiskLevel.Dangerous, CommandReadWriteKind.WritesWorkspace, CommandLockKind.WorkspaceWrite, [Scope(), Flag("--yes", "确认清空")], requiresConfirmation: true),
            Command("session.list", "列出 Sessions", ["session", "list"], CommandRiskLevel.Safe, CommandReadWriteKind.ReadOnly, CommandLockKind.WorkspaceRead),
            Command("session.show", "显示 Session", ["session", "show"], CommandRiskLevel.Safe, CommandReadWriteKind.ReadOnly, CommandLockKind.WorkspaceRead, [Position("session-id", "Session ID")]),
            Command("session.export", "导出 Session", ["session", "export"], CommandRiskLevel.Safe, CommandReadWriteKind.ReadOnly, CommandLockKind.WorkspaceRead, [Position("session-id", "Session ID"), Path("--output", "输出文件")]),
            Command("session.archive", "归档 Session", ["session", "archive"], CommandRiskLevel.Caution, CommandReadWriteKind.WritesRuntimeState, CommandLockKind.WorkspaceWrite, [Position("session-id", "Session ID")]),
            Command("session.unarchive", "取消归档 Session", ["session", "unarchive"], CommandRiskLevel.Caution, CommandReadWriteKind.WritesRuntimeState, CommandLockKind.WorkspaceWrite, [Position("session-id", "Session ID")]),
            Command("session.compact", "压缩 Session", ["session", "compact"], CommandRiskLevel.Caution, CommandReadWriteKind.WritesRuntimeState, CommandLockKind.WorkspaceWrite, [Position("session-id", "Session ID")]),
            Command("session.delete", "删除 Session", ["session", "delete"], CommandRiskLevel.Dangerous, CommandReadWriteKind.WritesRuntimeState, CommandLockKind.WorkspaceWrite, [Position("session-id", "Session ID"), Flag("--confirm", "确认删除")], requiresConfirmation: true),
        ]),
        new CommandGroup("skills", "Skills", "按源码提供的动态 Skill 命令。",
        [
            Skill("skill.list", "列出 Skills", ["skill", "list"]),
            Skill("skill.show", "显示 Skill", ["skill", "show"], [Position("skill-name", "Skill 名称")]),
            Skill("skill.validate", "验证 Skills", ["skill", "validate"]),
            Skill("skill.refresh", "刷新 Skills", ["skill", "refresh"]),
        ]),
        new CommandGroup("database", "数据库维护", "SQLite 状态检查与维护。",
        [
            Command("db.check", "检查数据库", ["db", "check"], CommandRiskLevel.Safe, CommandReadWriteKind.ReadOnly, CommandLockKind.WorkspaceRead),
            Command("db.repair", "修复数据库", ["db", "repair"], CommandRiskLevel.Dangerous, CommandReadWriteKind.WritesRuntimeState, CommandLockKind.WorkspaceWrite, [Flag("--dry-run", "仅预览"), Flag("--yes", "确认修复")], requiresConfirmation: true, preferDryRun: true),
            Command("db.rebuild", "重建数据库", ["db", "rebuild"], CommandRiskLevel.Dangerous, CommandReadWriteKind.WritesRuntimeState, CommandLockKind.WorkspaceWrite, [Flag("--dry-run", "仅预览"), Flag("--confirm", "确认重建")], requiresConfirmation: true, preferDryRun: true),
            Command("db.vacuum", "压缩数据库", ["db", "vacuum"], CommandRiskLevel.Dangerous, CommandReadWriteKind.WritesRuntimeState, CommandLockKind.WorkspaceWrite, requiresConfirmation: true),
            Command("db.backup", "备份数据库", ["db", "backup"], CommandRiskLevel.Caution, CommandReadWriteKind.WritesRuntimeState, CommandLockKind.WorkspaceRead, [Path("--output", "备份路径", required: true)]),
            Command("db.migrate", "迁移数据库", ["db", "migrate"], CommandRiskLevel.Dangerous, CommandReadWriteKind.WritesRuntimeState, CommandLockKind.WorkspaceWrite, [Text("--target", "目标版本"), Flag("--dry-run", "仅预览")], requiresConfirmation: true, preferDryRun: true),
        ]),
        new CommandGroup("storage", "存储维护", "Messages 与 Blob 存储。",
        [
            Command("storage.check", "检查存储", ["storage", "check"], CommandRiskLevel.Safe, CommandReadWriteKind.ReadOnly, CommandLockKind.WorkspaceRead),
            Command("storage.gc", "清理存储", ["storage", "gc"], CommandRiskLevel.Dangerous, CommandReadWriteKind.WritesRuntimeState, CommandLockKind.WorkspaceWrite, [Flag("--dry-run", "仅预览"), Flag("--confirm", "确认清理")], requiresConfirmation: true, preferDryRun: true),
        ]),
        new CommandGroup("control", "在线控制", "控制已运行的 Runtime。",
        [
            Control("ctl.status", "Runtime 状态", ["ctl", "status"]),
            Control("ctl.sessions", "活动 Sessions", ["ctl", "sessions"]),
            Control("ctl.runs", "活动 Runs", ["ctl", "runs"]),
            Command("ctl.cancel", "取消 Run", ["ctl", "cancel"], CommandRiskLevel.Caution, CommandReadWriteKind.WritesRuntimeState, CommandLockKind.RuntimeConnection, [Position("run-id", "Run ID")], usesRuntimeSecret: true),
            Command("ctl.credential.update", "更新 Provider 凭据", ["ctl", "credential", "update"], CommandRiskLevel.Caution, CommandReadWriteKind.WritesRuntimeState, CommandLockKind.RuntimeConnection,
                [Text("--run", "Run ID", required: true), Text("--provider", "Provider ID", required: true), SecretEnv("--api-key-env", "API Key 环境变量名"), Text("--profile", "Provider Profile"), DateTime("--expires-at", "过期时间")], usesRuntimeSecret: true),
        ]),
    ]);

    private static CommandDefinition Command(
        string id,
        string displayName,
        string[] path,
        CommandRiskLevel risk,
        CommandReadWriteKind readWrite,
        CommandLockKind lockKind,
        ParameterDefinition[]? parameters = null,
        bool requiresConfirmation = false,
        bool preferDryRun = false,
        bool usesRuntimeSecret = false,
        bool isRepl = false,
        string? note = null) => new(
            id, displayName, path, risk, readWrite, lockKind, parameters ?? [],
            ["exitCode"], requiresConfirmation, preferDryRun, usesRuntimeSecret, isRepl, note);

    private static CommandDefinition Skill(
        string id,
        string name,
        string[] path,
        ParameterDefinition[]? parameters = null) => Command(
            id, name, path, CommandRiskLevel.Safe, CommandReadWriteKind.ReadOnly,
            CommandLockKind.WorkspaceRead, parameters,
            note: "超出冻结矩阵，按当前源码实现；可用 --help 动态补充。");

    private static CommandDefinition Control(string id, string name, string[] path) => Command(
        id, name, path, CommandRiskLevel.Safe, CommandReadWriteKind.ReadOnly,
        CommandLockKind.RuntimeConnection, usesRuntimeSecret: true);

    private static ParameterDefinition Text(string name, string prompt, bool required = false) =>
        new(name, prompt, Required: required);

    private static ParameterDefinition Position(string name, string prompt) =>
        new(name, prompt, Required: true, Positional: true);

    private static ParameterDefinition Flag(string name, string prompt) =>
        new(name, prompt, ParameterKind.Boolean);

    private static ParameterDefinition Path(string name, string prompt, bool required = false) =>
        new(name, prompt, ParameterKind.Path, Required: required);

    private static ParameterDefinition Enum(string name, string prompt, string[] allowed) =>
        new(name, prompt, ParameterKind.Enum, AllowedValues: allowed);

    private static ParameterDefinition Number(string name, string prompt, int minimum, int maximum) =>
        new(name, prompt, ParameterKind.WholeNumber, Minimum: minimum, Maximum: maximum);

    private static ParameterDefinition DateTime(string name, string prompt) =>
        new(name, prompt, ParameterKind.DateTimeIso8601);

    private static ParameterDefinition SecretEnv(string name, string prompt) =>
        new(name, prompt, ParameterKind.SecretEnvName, Required: true);

    private static ParameterDefinition Stdin(string name, string prompt) =>
        new(name, prompt, ParameterKind.StdinLine, Required: true);

    private static ParameterDefinition Scope() => new(
        "scope", "Memory scope", ParameterKind.Enum, Required: true, Positional: true,
        DefaultValue: "global", AllowedValues: ["global", "project"]);
}
