using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Persistence.Sqlite;

/// <summary>
/// 同一 <see cref="SqliteConnection"/> 实例上的跨仓储写/读串行化闸门。
/// Microsoft.Data.Sqlite 连接非线程安全；Work 步骤并发与 ToolGateway 可能同时触达
/// Session / Work / ToolIntent / Outbox，必须共用一把锁。
/// </summary>
internal static class SqliteConnectionSync
{
    private static readonly ConditionalWeakTable<SqliteConnection, SemaphoreSlim> Gates = new();

    /// <summary>获取连接级互斥闸门；连接存活期间不主动 Dispose。</summary>
    public static SemaphoreSlim Get(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return Gates.GetValue(connection, static _ => new SemaphoreSlim(1, 1));
    }
}
