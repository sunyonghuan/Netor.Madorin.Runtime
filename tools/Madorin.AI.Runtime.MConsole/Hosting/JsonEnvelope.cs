using System.Text.Json;

namespace Madorin.AI.Runtime.MConsole.Hosting;

public sealed record JsonEnvelope(
    bool Success,
    JsonElement? Data,
    JsonElement? Error,
    IReadOnlyList<string> Warnings,
    string? DiagnosticId)
{
    public static bool TryParse(string standardOutput, out JsonEnvelope? envelope, out string? error)
    {
        envelope = null;
        error = null;

        if (string.IsNullOrWhiteSpace(standardOutput))
        {
            error = "stdout is empty.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(standardOutput);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("success", out var successElement)
                || successElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                error = "stdout is JSON but does not contain a boolean 'success' field.";
                return false;
            }

            var warnings = ReadWarnings(root);
            envelope = new JsonEnvelope(
                successElement.GetBoolean(),
                CloneOptional(root, "data"),
                CloneOptional(root, "error"),
                warnings,
                ReadString(root, "diagnosticId"));
            return true;
        }
        catch (JsonException ex)
        {
            error = $"stdout is not a valid JSON envelope: {ex.Message}";
            return false;
        }
    }

    private static JsonElement? CloneOptional(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.Clone()
            : null;

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string[] ReadWarnings(JsonElement root)
    {
        if (!root.TryGetProperty("warnings", out var warnings)
            || warnings.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return warnings.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToArray();
    }
}
