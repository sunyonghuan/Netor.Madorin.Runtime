using System.Text.Json.Serialization;
using Madorin.AI.Runtime.Contracts.Expert;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ExpertPlan))]
internal sealed partial class SimulationJsonContext : JsonSerializerContext;
