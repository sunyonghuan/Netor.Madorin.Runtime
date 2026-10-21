using System.Text.Json.Nodes;
using Madorin.AI.Multimedia.Core.Protocol;
using static Madorin.AI.Multimedia.Core.Protocol.JsonProtocol;

namespace Madorin.AI.Multimedia.Core.Files;

public sealed class ArtifactStore(WorkspacePaths paths) : IDisposable
{
    private readonly List<string> _writtenFiles = [];

    public async Task<JsonObject> WriteImageAsync(byte[] bytes, string format, string outputDirectory, int index, CancellationToken cancellationToken)
    {
        var dimensions = await ImageValidator.ValidateAsync(bytes, format, cancellationToken).ConfigureAwait(false);
        if (dimensions.Width < 1 || dimensions.Height < 1)
        {
            throw new SkillException("provider_error", "返回的图片格式或尺寸无效。");
        }

        var target = paths.Resolve(outputDirectory);
        var extension = format == "jpeg" ? "jpg" : format;
        var temporary = Path.Combine(target, ".image-" + Guid.NewGuid().ToString("N") + ".partial");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            paths.EnsureAllowed(target);
            Directory.CreateDirectory(target);
            paths.EnsureAllowed(temporary);
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            for (var sequence = index; ; sequence++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = $"image_{sequence:000}.{extension}";
                var path = paths.Resolve(name, target);
                try
                {
                    File.Move(temporary, path);
                }
                catch (IOException) when (File.Exists(path))
                {
                    continue;
                }

                _writtenFiles.Add(path);
                return Obj(("index", N(index)), ("path", S(name)), ("media_type", S("image/" + format)),
                    ("format", S(extension)), ("size_bytes", N(bytes.LongLength)), ("width", N(dimensions.Width)), ("height", N(dimensions.Height)));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new SkillException("file_write_error", "无法写入图片产物。");
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public void Commit() => _writtenFiles.Clear();

    public void Dispose()
    {
        foreach (var path in _writtenFiles)
        {
            TryDelete(path);
        }

        _writtenFiles.Clear();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 清理失败不能掩盖原始错误。
        }
    }
}
