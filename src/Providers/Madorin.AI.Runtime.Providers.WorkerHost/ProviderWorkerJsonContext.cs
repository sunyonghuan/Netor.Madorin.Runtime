using System.Text.Json.Serialization;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Providers.WorkerHost;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ProviderWorkerFrame))]
[JsonSerializable(typeof(HelloFrame))]
[JsonSerializable(typeof(HelloOkFrame))]
[JsonSerializable(typeof(HelloRejectedFrame))]
[JsonSerializable(typeof(InvokeFrame))]
[JsonSerializable(typeof(EventFrame))]
[JsonSerializable(typeof(CancelFrame))]
[JsonSerializable(typeof(PingFrame))]
[JsonSerializable(typeof(PongFrame))]
[JsonSerializable(typeof(ShutdownFrame))]
[JsonSerializable(typeof(ErrorFrame))]
[JsonSerializable(typeof(ProviderConnectionConfig))]
[JsonSerializable(typeof(ProviderCapabilities))]
[JsonSerializable(typeof(ProviderModel))]
[JsonSerializable(typeof(ProviderModel[]))]
[JsonSerializable(typeof(RuntimeProviderRequest))]
[JsonSerializable(typeof(ExpertRequestControl))]
[JsonSerializable(typeof(RuntimeProviderMessage))]
[JsonSerializable(typeof(RuntimeProviderMessage[]))]
[JsonSerializable(typeof(RuntimeProviderEvent))]
[JsonSerializable(typeof(TextDeltaProviderEvent))]
[JsonSerializable(typeof(ReasoningDeltaProviderEvent))]
[JsonSerializable(typeof(ToolCallDeltaProviderEvent))]
[JsonSerializable(typeof(ToolCallCompleteProviderEvent))]
[JsonSerializable(typeof(UsageUpdatedProviderEvent))]
[JsonSerializable(typeof(ProjectionAdjustedProviderEvent))]
[JsonSerializable(typeof(InvocationCompletedProviderEvent))]
[JsonSerializable(typeof(InvocationFailedProviderEvent))]
[JsonSerializable(typeof(RuntimeStructuredOutput))]
[JsonSerializable(typeof(RuntimeError))]
[JsonSerializable(typeof(ToolDescriptor))]
[JsonSerializable(typeof(ToolDescriptor[]))]
[JsonSerializable(typeof(ContentBlock))]
[JsonSerializable(typeof(ContentBlock[]))]
[JsonSerializable(typeof(TextContentBlock))]
[JsonSerializable(typeof(ReasoningContentBlock))]
[JsonSerializable(typeof(ToolCallContentBlock))]
[JsonSerializable(typeof(ToolResultContentBlock))]
[JsonSerializable(typeof(BlobRefContentBlock))]
[JsonSerializable(typeof(BlobReference))]
[JsonSerializable(typeof(ProviderExtensionData))]
[JsonSerializable(typeof(ProviderExtensionData[]))]
public sealed partial class ProviderWorkerJsonContext : JsonSerializerContext;
