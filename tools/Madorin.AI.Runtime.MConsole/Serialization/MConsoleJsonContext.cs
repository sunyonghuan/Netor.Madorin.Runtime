using System.Text.Json.Serialization;

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
public sealed partial class MConsoleJsonContext : JsonSerializerContext;
