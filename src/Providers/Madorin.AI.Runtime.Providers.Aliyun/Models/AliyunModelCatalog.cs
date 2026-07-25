using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Providers.Aliyun;

public static class AliyunModelCatalog
{
    public const string DefaultBaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1";
    public const string DefaultModelId = "qwen-plus";

    public static IReadOnlyList<ProviderModel> All { get; } =
    [
        new(
            DefaultModelId,
            "Qwen Plus",
            Capabilities: new ProviderCapabilities(Streaming: true))
    ];
}
