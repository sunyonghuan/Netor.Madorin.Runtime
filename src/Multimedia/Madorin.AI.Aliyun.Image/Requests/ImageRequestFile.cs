using System.Text.Json.Nodes;

namespace Madorin.AI.Aliyun.Image.Requests;

internal sealed record ImageRequestFile(JsonObject Content, string BaseDirectory);

