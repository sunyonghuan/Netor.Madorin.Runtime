namespace Madorin.AI.Runtime.MConsole.Menu;

public enum CommandRiskLevel
{
    Safe,
    Caution,
    Dangerous,
}

public enum CommandReadWriteKind
{
    ReadOnly,
    WritesConfiguration,
    WritesWorkspace,
    WritesRuntimeState,
    ProcessLifecycle,
}

public enum CommandLockKind
{
    None,
    WorkspaceRead,
    WorkspaceWrite,
    RuntimeConnection,
}

public enum ParameterKind
{
    Text,
    Path,
    Enum,
    Boolean,
    WholeNumber,
    DateTimeIso8601,
    SecretEnvName,
    StdinLine,
}

public sealed record ParameterDefinition(
    string Name,
    string Prompt,
    ParameterKind Kind = ParameterKind.Text,
    bool Required = false,
    bool Positional = false,
    string? DefaultValue = null,
    string[]? AllowedValues = null,
    string[]? MutuallyExclusiveWith = null,
    int? Minimum = null,
    int? Maximum = null,
    bool MustExist = false);

public sealed record CommandDefinition(
    string Id,
    string DisplayName,
    string[] CommandPath,
    CommandRiskLevel RiskLevel,
    CommandReadWriteKind ReadWriteKind,
    CommandLockKind LockKind,
    ParameterDefinition[] Parameters,
    string[] DefaultAssertions,
    bool RequiresConfirmation = false,
    bool PreferDryRun = false,
    bool UsesRuntimeSecret = false,
    bool IsRepl = false,
    string? ContractNote = null);

public sealed record CommandGroup(
    string Id,
    string Title,
    string Description,
    CommandDefinition[] Items);

public sealed record CommandInvocation(
    CommandDefinition Definition,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string?> Values,
    string? StandardInput,
    IReadOnlyDictionary<string, string?> Environment);

public sealed record CommandValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static CommandValidationResult Success { get; } = new(true, []);
}
