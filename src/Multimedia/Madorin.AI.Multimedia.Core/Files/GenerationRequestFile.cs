using System.Text.Json.Nodes;

namespace Madorin.AI.Multimedia.Core.Files;

public sealed record GenerationRequestFile(JsonObject Content, string BaseDirectory);
