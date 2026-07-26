namespace Madorin.AI.Runtime.Persistence.Sqlite;

/// <summary>Test-only failure points for atomic Run terminal commits.</summary>
public enum SqliteSessionRepositoryFailurePoint
{
    BeforeTerminalTransaction,
    BeforeTerminalCommit
}
