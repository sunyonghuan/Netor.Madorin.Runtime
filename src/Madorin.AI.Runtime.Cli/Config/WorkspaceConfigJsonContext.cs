using System.Text.Json.Serialization;

namespace Madorin.AI.Runtime.Cli.Config;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(WorkspaceConfig))]
internal sealed partial class WorkspaceConfigJsonContext : JsonSerializerContext
{
}
