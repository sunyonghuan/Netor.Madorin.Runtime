using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Madorin.AI.Runtime.Services.Tools;

/// <summary>Canonicalizes the scope bound to one approved Skill script invocation.</summary>
public static class SkillScriptAuthorization
{
    public const string ToolId = "builtin.skill.script.run";

    public static bool TryReadScope(
        string argumentsJson,
        out string skillName,
        out string scriptHash,
        out string interpreter,
        out string catalogVersion)
    {
        skillName = string.Empty;
        scriptHash = string.Empty;
        interpreter = string.Empty;
        catalogVersion = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            skillName = root.GetProperty("skillName").GetString() ?? string.Empty;
            scriptHash = root.GetProperty("scriptHash").GetString() ?? string.Empty;
            interpreter = root.GetProperty("interpreter").GetString() ?? string.Empty;
            catalogVersion = root.GetProperty("catalogVersion").GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(skillName)
                && !string.IsNullOrWhiteSpace(scriptHash)
                && !string.IsNullOrWhiteSpace(interpreter)
                && !string.IsNullOrWhiteSpace(catalogVersion);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    public static string ComputeScriptHash(string materializedScriptPath) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(materializedScriptPath)));

    public static string CreateKey(string skillName, string scriptHash, string interpreter) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{skillName}\n{scriptHash}\n{interpreter}")));
}
