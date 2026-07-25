using System.Text.Json.Serialization;

namespace Madorin.AI.Runtime.Providers.Aliyun;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(AliyunModelCatalogResponse))]
[JsonSerializable(typeof(AliyunModelCatalogItem))]
internal sealed partial class AliyunJsonContext : JsonSerializerContext
{
}
