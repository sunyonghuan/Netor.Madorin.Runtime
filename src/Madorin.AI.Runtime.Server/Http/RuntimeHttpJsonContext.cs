using System.Text.Json;
using System.Text.Json.Serialization;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Http;

namespace Madorin.AI.Runtime.Server.Http;

/// <summary>
/// JSON serialization context for HTTP API types.
/// Required for Native AOT and trimming support.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(HttpRunRequest))]
[JsonSerializable(typeof(HttpRunResponse))]
[JsonSerializable(typeof(ExpertRequestControl))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(HealthResponse))]
internal sealed partial class RuntimeHttpJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Health endpoint response type.
/// </summary>
internal sealed record HealthResponse
{
    public required string Status { get; init; }
}
