using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace Madorin.AI.Runtime.Persistence.Files;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ConversationHeader))]
[JsonSerializable(typeof(ConversationRecordV1))]
[JsonSerializable(typeof(ConversationCompactCacheV1))]
[JsonSerializable(typeof(SessionMaintenanceAuditRecord))]
internal sealed partial class ConversationJsonContext : JsonSerializerContext
{
    public static ConversationJsonContext Persistence => PersistenceContext.Instance;

    private static class PersistenceContext
    {
        internal static readonly ConversationJsonContext Instance = new(
            new JsonSerializerOptions(Default.Options)
            {
                Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
            });
    }
}

internal sealed record ConversationHeader(
    string Schema,
    string SessionId,
    string Mode,
    DateTimeOffset CreatedAt);
