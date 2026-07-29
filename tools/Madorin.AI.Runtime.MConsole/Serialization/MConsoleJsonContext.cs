using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

using Madorin.AI.Runtime.MConsole.Configuration;
using Madorin.AI.Runtime.MConsole.Callbacks;
using Madorin.AI.Runtime.MConsole.HostClient;
using Madorin.AI.Runtime.MConsole.Recovery;
using Madorin.AI.Runtime.MConsole.Testing;

namespace Madorin.AI.Runtime.MConsole.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(MConsoleOptions))]
[JsonSerializable(typeof(RuntimeCursorState))]
[JsonSerializable(typeof(StoredToolCallResult))]
[JsonSerializable(typeof(ToolCatalogDraft))]
[JsonSerializable(typeof(HostRecoveryState))]
[JsonSerializable(typeof(ExecutionHistoryEntry))]
[JsonSerializable(typeof(ExecutionBaseline))]
[JsonSerializable(typeof(MConsoleTestCase))]
[JsonSerializable(typeof(TestCaseReport))]
public sealed partial class MConsoleJsonContext : JsonSerializerContext
{
    public static MConsoleJsonContext Persistence => PersistenceContext.Instance;

    private static class PersistenceContext
    {
        internal static readonly MConsoleJsonContext Instance = new(
            new JsonSerializerOptions(Default.Options)
            {
                Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
            });
    }
}
