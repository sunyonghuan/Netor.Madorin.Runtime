using System.Text.Json;
using System.Text.RegularExpressions;

namespace Madorin.AI.Runtime.MConsole.Testing;

public sealed partial class CaseExecutionContext
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _secretKeys = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> Values => _values;

    public void Set(string key, string value, bool isSecret = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        _values[key] = value;
        if (isSecret)
        {
            _secretKeys.Add(key);
        }
    }

    public string Resolve(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return PlaceholderRegex().Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            return _values.TryGetValue(key, out var value)
                ? value
                : throw new InvalidDataException($"Case variable '{key}' is not defined.");
        });
    }

    public Dictionary<string, string?> Resolve(
        IReadOnlyDictionary<string, string?> arguments) => arguments.ToDictionary(
            static pair => pair.Key,
            pair => pair.Value is null ? null : Resolve(pair.Value),
            StringComparer.Ordinal);

    public void CaptureStepData(string stepId, JsonElement data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        CaptureElement($"step.{stepId}.data", data);
        if (data.ValueKind is JsonValueKind.Object)
        {
            foreach (var property in data.EnumerateObject())
            {
                if (property.Value.ValueKind is JsonValueKind.String)
                {
                    Set(property.Name, property.Value.GetString()!);
                }
                else if (property.Value.ValueKind is JsonValueKind.Number
                    or JsonValueKind.True
                    or JsonValueKind.False)
                {
                    Set(property.Name, property.Value.GetRawText());
                }
            }
        }
    }

    public IReadOnlyDictionary<string, string> ExportReportVariables() => _values
        .Where(pair => !_secretKeys.Contains(pair.Key))
        .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);

    private void CaptureElement(string path, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    CaptureElement(path + "." + property.Name, property.Value);
                }

                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    CaptureElement($"{path}.{index++}", item);
                }

                break;
            case JsonValueKind.String:
                Set(path, element.GetString()!);
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                Set(path, element.GetRawText());
                break;
        }
    }

    [GeneratedRegex("\\$\\{([A-Za-z0-9_.-]+)\\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();
}
