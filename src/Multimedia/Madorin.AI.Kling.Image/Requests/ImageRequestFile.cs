using System.Text.Json.Nodes;

namespace Madorin.AI.Kling.Image.Requests;

internal sealed record ImageRequestFile(JsonObject Content, string BaseDirectory);
