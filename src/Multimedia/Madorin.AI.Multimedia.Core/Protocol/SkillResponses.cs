using System.Text;
using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Execution;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.Multimedia.Core.Protocol;

public static class SkillResponses
{
    public static JsonObject Failure(string provider, SkillInvocation invocation, string code, string message, string? providerCode = null)
    {
        var providerError = providerCode is null ? null : Obj(("code", S(providerCode)), ("message", S(message)));
        return Obj(("schema_version", S(SkillProtocol.ContractVersion)), ("response_id", S(invocation.ResponseId)), ("status", S("failed")),
            ("task_id", S(invocation.TaskId)), ("task_context", invocation.TaskContext?.DeepClone()), ("error", Obj(("code", S(code)), ("message", S(message)), ("provider_error", providerError))),
            ("provider_metadata", ProviderMetadata(provider, invocation)),
            ("ignored_parameters", invocation.IgnoredParameters.DeepClone()), ("converted_parameters", invocation.ConvertedParameters.DeepClone()));
    }

    public static JsonObject Invalid(string provider, SkillInvocation invocation, string code, string message) =>
        Obj(("schema_version", S(SkillProtocol.ContractVersion)), ("response_id", S(invocation.ResponseId)), ("status", S("invalid")), ("mode", S("try_run")),
            ("provider", S(provider)), ("model", S(invocation.Model)), ("input_parameters", new JsonObject()), ("effective_parameters", new JsonObject()),
            ("credential_presence", Obj(("api_key", B(invocation.CredentialPresent)))), ("request_preview", null), ("unicode_checks", new JsonArray()),
            ("errors", new JsonArray(Obj(("parameter", null), ("code", S(code)), ("message", S(message))))),
            ("ignored_parameters", invocation.IgnoredParameters.DeepClone()), ("converted_parameters", invocation.ConvertedParameters.DeepClone()), ("side_effects", NoSideEffects()));

    public static JsonObject Generated(string provider, string mediaType, SkillInvocation invocation, JsonArray artifacts) =>
        Obj(("schema_version", S(SkillProtocol.ContractVersion)), ("response_id", S(invocation.ResponseId)), ("status", S("success")),
            ("task_id", S(invocation.TaskId)), ("task_context", invocation.TaskContext?.DeepClone()), ("type", S(mediaType)), ("artifacts", artifacts), ("errors", new JsonArray()),
            ("provider_metadata", ProviderMetadata(provider, invocation)), ("ignored_parameters", invocation.IgnoredParameters.DeepClone()), ("converted_parameters", invocation.ConvertedParameters.DeepClone()));

    public static JsonObject ProviderMetadata(string provider, SkillInvocation invocation) =>
        Obj(("provider", S(provider)), ("model", S(invocation.Model)), ("request_id", S(invocation.ProviderRequestId)),
            ("generation_time_ms", N(invocation.ElapsedMilliseconds)), ("remote_status", S(invocation.RemoteStatus)), ("remote_cancellation", S(invocation.RemoteCancellation)));

    public static JsonObject NoSideEffects() => Obj(("network_request", B(false)), ("output_files_written", B(false)), ("directories_created", B(false)));

    public static JsonArray UnicodeChecks(JsonNode input)
    {
        var result = new JsonArray();
        Collect(input, string.Empty, result);
        return result;
    }

    private static void Collect(JsonNode? node, string path, JsonArray result)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                if (pair.Key is not ("api_key" or "api_url"))
                {
                    Collect(pair.Value, path + "/" + EscapePointer(pair.Key), result);
                }
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                Collect(array[index], path + "/" + index, result);
            }
        }
        else if (node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0)
        {
            if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Query.Length > 0 || uri.UserInfo.Length > 0))
            {
                return;
            }

            var codepoints = new JsonArray(text.EnumerateRunes().Select(rune => (JsonNode?)S($"U+{rune.Value:X4}")).ToArray());
            result.Add((JsonNode)Obj(("parameter", S(path)), ("display_value", S(text)), ("unicode_codepoints", codepoints),
                ("utf8_roundtrip", B(Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text)) == text))));
        }
    }
}
