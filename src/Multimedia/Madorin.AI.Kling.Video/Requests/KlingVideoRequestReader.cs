using Madorin.AI.Multimedia.Core.Execution;
using Madorin.AI.Multimedia.Core.Files;

namespace Madorin.AI.Kling.Video.Requests;

internal sealed class KlingVideoRequestReader(WorkspacePaths paths, SkillInvocation invocation)
{
    public async Task<VideoRequestFile> ReadAsync(string file, CancellationToken cancellationToken)
    {
        var result = await new GenerationRequestReader(paths, invocation).ReadAsync(file, cancellationToken);
        return new(result.Content, result.BaseDirectory);
    }
}
