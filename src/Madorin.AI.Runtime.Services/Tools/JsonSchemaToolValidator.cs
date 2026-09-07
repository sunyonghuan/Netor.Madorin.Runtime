using System.Collections.Concurrent;
using System.Text.Json;
using Json.Schema;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Services.Tools;

/// <summary>Validates tool payloads with cached JSON Schema 2020-12 schemas.</summary>
public sealed class JsonSchemaToolValidator : IToolSchemaValidator
{
    private readonly ConcurrentDictionary<string, JsonSchema> _schemas =
        new(StringComparer.Ordinal);

    public ToolSchemaValidationResult ValidateDescriptor(ToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        try
        {
            _ = GetSchema(descriptor.InputSchemaJson);
            _ = GetSchema(descriptor.OutputSchemaJson);
            return ToolSchemaValidationResult.Valid;
        }
        catch (JsonException ex)
        {
            return new ToolSchemaValidationResult(false, ex.Message);
        }
        catch (ArgumentException ex)
        {
            return new ToolSchemaValidationResult(false, ex.Message);
        }
    }

    public ToolSchemaValidationResult ValidateArguments(
        ToolDescriptor descriptor,
        JsonElement arguments) =>
        Evaluate(descriptor.InputSchemaJson, arguments, "arguments");

    public ToolSchemaValidationResult ValidateResult(
        ToolDescriptor descriptor,
        JsonElement result) =>
        Evaluate(descriptor.OutputSchemaJson, result, "result");

    private ToolSchemaValidationResult Evaluate(
        string schemaJson,
        JsonElement instance,
        string payloadName)
    {
        try
        {
            var evaluation = GetSchema(schemaJson).Evaluate(
                instance,
                new EvaluationOptions { OutputFormat = OutputFormat.List });
            if (evaluation.IsValid)
            {
                return ToolSchemaValidationResult.Valid;
            }

            return new ToolSchemaValidationResult(
                false,
                BuildArgumentsError(payloadName, schemaJson));
        }
        catch (JsonException ex)
        {
            return new ToolSchemaValidationResult(false, ex.Message);
        }
        catch (ArgumentException ex)
        {
            return new ToolSchemaValidationResult(false, ex.Message);
        }
    }

    private static string BuildArgumentsError(string payloadName, string schemaJson)
    {
        var hint = TryDescribeExpectedObjectShape(schemaJson);
        return string.IsNullOrWhiteSpace(hint)
            ? $"Tool {payloadName} does not satisfy its JSON Schema."
            : $"Tool {payloadName} does not satisfy its JSON Schema. Expected object fields: {hint}.";
    }

    /// <summary>
    /// Best-effort short hint from a JSON Schema object: required names, else property names.
    /// </summary>
    internal static string? TryDescribeExpectedObjectShape(string schemaJson)
    {
        try
        {
            using var document = JsonDocument.Parse(schemaJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("required", out var required)
                && required.ValueKind == JsonValueKind.Array
                && required.GetArrayLength() > 0)
            {
                return JoinNames(required, max: 8);
            }

            if (root.TryGetProperty("properties", out var properties)
                && properties.ValueKind == JsonValueKind.Object)
            {
                var names = new List<string>();
                foreach (var property in properties.EnumerateObject())
                {
                    names.Add(property.Name);
                    if (names.Count >= 8)
                    {
                        break;
                    }
                }

                return names.Count == 0 ? null : string.Join(", ", names);
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static string JoinNames(JsonElement array, int max)
    {
        var names = new List<string>(Math.Min(max, array.GetArrayLength()));
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var name = item.GetString();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            names.Add(name);
            if (names.Count >= max)
            {
                break;
            }
        }

        return names.Count == 0 ? string.Empty : string.Join(", ", names);
    }

    private JsonSchema GetSchema(string schemaJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaJson);
        return _schemas.GetOrAdd(
            schemaJson,
            static json => JsonSchema.FromText(
                json,
                new BuildOptions { SchemaRegistry = new SchemaRegistry() }));
    }
}
