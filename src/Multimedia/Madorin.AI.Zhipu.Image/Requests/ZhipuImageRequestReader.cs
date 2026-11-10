using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Files;

namespace Madorin.AI.Zhipu.Image.Requests;

internal sealed class ZhipuImageRequestReader(WorkspacePaths paths, SkillInvocation invocation)
{
    public async Task<ImageRequestFile> ReadAsync(string file, CancellationToken cancellationToken, bool? tryRunOverride = null)
    {
        var result = await new GenerationRequestReader(paths, invocation).ReadAsync(file, cancellationToken, tryRunOverride);
        return new(result.Content, result.BaseDirectory);
    }
}
