using System.Text.Json.Nodes;

namespace Madorin.AI.Gemini.Image.Requests;

internal sealed record ImageRequestFile(JsonObject Content, string BaseDirectory);
