using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace Madorin.AI.Runtime.Cli.Config;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StandaloneConfig))]
[JsonSerializable(typeof(AgentConfig))]
[JsonSerializable(typeof(RuntimeInstanceInfo))]
public sealed partial class ConfigJsonContext : JsonSerializerContext
{
    public static ConfigJsonContext Persistence => PersistenceContext.Instance;

    private static class PersistenceContext
    {
        internal static readonly ConfigJsonContext Instance = new(
            new JsonSerializerOptions(Default.Options)
            {
                Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
            });
    }
}
