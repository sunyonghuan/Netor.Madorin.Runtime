using System.Text.Json.Nodes;

namespace Madorin.AI.Bfl.Image.Requests;

internal sealed record ImageRequestFile(JsonObject Content, string BaseDirectory);
