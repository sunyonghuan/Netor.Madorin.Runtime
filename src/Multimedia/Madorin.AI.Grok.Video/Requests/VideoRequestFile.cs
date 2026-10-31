using System.Text.Json.Nodes;

namespace Madorin.AI.Grok.Video.Requests;

internal sealed record VideoRequestFile(JsonObject Content, string BaseDirectory);
