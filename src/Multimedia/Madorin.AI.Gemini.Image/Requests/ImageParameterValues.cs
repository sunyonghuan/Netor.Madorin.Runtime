using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Protocol;

namespace Madorin.AI.Gemini.Image.Requests;

internal static class ImageParameterValues
{
    public static string? Text(JsonObject value, string name, string? fallback = null) => ParameterValues.Text(value, name, fallback);
    public static int Integer(JsonObject value, string name, int fallback) => ParameterValues.PositiveNumber(value, name, fallback);
    public static bool Boolean(JsonObject value, string name, bool fallback = false) => ParameterValues.Boolean(value, name, fallback);
}
