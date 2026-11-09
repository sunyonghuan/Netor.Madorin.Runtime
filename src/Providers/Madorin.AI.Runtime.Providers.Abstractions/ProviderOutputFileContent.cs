using Microsoft.Extensions.AI;

namespace Madorin.AI.Runtime.Providers.Abstractions;

/// <summary>完整下载的独立文件；音频文件也不能与流式音频片段合并。</summary>
public sealed class ProviderOutputFileContent(DataContent file) : AIContent
{
    public DataContent File { get; } = file;
}
