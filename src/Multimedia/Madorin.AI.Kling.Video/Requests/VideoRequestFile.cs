using System.Text.Json.Nodes;

namespace Madorin.AI.Kling.Video.Requests;

internal sealed record VideoRequestFile(JsonObject Content, string BaseDirectory);
