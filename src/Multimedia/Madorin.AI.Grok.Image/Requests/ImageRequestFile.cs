using System.Text.Json.Nodes;

namespace Madorin.AI.Grok.Image.Requests;

internal sealed record ImageRequestFile(JsonObject Content, string BaseDirectory);
