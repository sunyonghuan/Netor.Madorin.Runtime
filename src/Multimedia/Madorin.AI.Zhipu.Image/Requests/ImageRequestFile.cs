using System.Text.Json.Nodes;

namespace Madorin.AI.Zhipu.Image.Requests;

internal sealed record ImageRequestFile(JsonObject Content, string BaseDirectory);
