using System.Text.Json.Nodes;

namespace Madorin.AI.OpenAI.Image.Requests;

internal sealed record ImageRequestFile(JsonObject Content, string BaseDirectory);
