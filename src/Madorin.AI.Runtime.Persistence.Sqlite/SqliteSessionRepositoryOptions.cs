namespace Madorin.AI.Runtime.Persistence.Sqlite;

/// <summary>Optional diagnostics and test hooks for the Session repository.</summary>
public sealed class SqliteSessionRepositoryOptions
{
    /// <summary>
    /// Invoked only at test failure points. Production callers should leave this unset.
    /// </summary>
    public Func<SqliteSessionRepositoryFailurePoint, CancellationToken, ValueTask>? FailureInjector { get; init; }
}
