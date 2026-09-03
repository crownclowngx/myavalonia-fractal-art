using System.Security.Cryptography;
using Avalonia.Media.Imaging;

namespace FractalArtPlugin.Application;

public interface IArtworkRenderPipeline
{
    Task<RgbaImage> RenderAsync(ArtworkDefinition artwork, RenderContext context, CancellationToken cancellationToken);
}

internal sealed class ArtworkRenderPipeline(
    IArtworkValidator validator,
    IJuliaFieldGenerator generator,
    IGradientMapper gradientMapper) : IArtworkRenderPipeline
{
    public async Task<RgbaImage> RenderAsync(
        ArtworkDefinition artwork,
        RenderContext context,
        CancellationToken cancellationToken)
    {
        validator.Validate(artwork);
        cancellationToken.ThrowIfCancellationRequested();
        var field = await generator.GenerateAsync(artwork.Julia, context, cancellationToken).ConfigureAwait(false);
        return gradientMapper.Map(field, artwork.Gradient, cancellationToken);
    }
}

public interface IPngEncoder
{
    byte[] Encode(RgbaImage image, CancellationToken cancellationToken);
}

public interface IAtomicFileWriter
{
    Task WriteAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);
}

public interface IArtworkExporter
{
    Task ExportAsync(ArtworkDefinition artwork, string path, CancellationToken cancellationToken);
}

internal sealed class ArtworkExporter(
    IArtworkRenderPipeline pipeline,
    IPngEncoder encoder,
    IAtomicFileWriter writer) : IArtworkExporter
{
    public async Task ExportAsync(ArtworkDefinition artwork, string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("导出路径不能为空。", nameof(path));
        }

        var image = await pipeline.RenderAsync(
            artwork,
            RenderContext.ForExport(artwork),
            cancellationToken).ConfigureAwait(false);
        var png = encoder.Encode(image, cancellationToken);
        await writer.WriteAsync(path, png, cancellationToken).ConfigureAwait(false);
    }
}

public interface IPreviewImageFactory
{
    Bitmap? Create(RgbaImage image, CancellationToken cancellationToken);
}

public interface IArtworkExportDialog
{
    Task<string?> PickPngPathAsync(string suggestedName, CancellationToken cancellationToken);
}

/// <summary>为测试和状态栏提供稳定指纹；它不参与缓存身份，也不替代作品的版本化配方。</summary>
internal static class RenderFingerprint
{
    public static string Create(RgbaImage image) =>
        Convert.ToHexString(SHA256.HashData(image.Pixels)).ToLowerInvariant()[..16];
}

public interface IArtworkHistory
{
    bool CanUndo { get; }
    bool CanRedo { get; }
    void Record(ArtworkDefinition previous);
    ArtworkDefinition Undo(ArtworkDefinition current);
    ArtworkDefinition Redo(ArtworkDefinition current);
    void Clear();
}

/// <summary>
/// 有界的作品快照历史。首阶段作品对象很小，使用不可变快照比引入复杂命令层更直观；
/// 以后增加大图层时可以在保持接口不变的前提下替换为差量历史。
/// </summary>
internal sealed class ArtworkHistory : IArtworkHistory
{
    private const int Capacity = 100;
    private readonly Stack<ArtworkDefinition> _undo = new();
    private readonly Stack<ArtworkDefinition> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Record(ArtworkDefinition previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        _undo.Push(previous);
        _redo.Clear();
        if (_undo.Count <= Capacity)
        {
            return;
        }

        var retained = _undo.Take(Capacity).Reverse().ToArray();
        _undo.Clear();
        foreach (var item in retained)
        {
            _undo.Push(item);
        }
    }

    public ArtworkDefinition Undo(ArtworkDefinition current)
    {
        if (!CanUndo)
        {
            return current;
        }

        _redo.Push(current);
        return _undo.Pop();
    }

    public ArtworkDefinition Redo(ArtworkDefinition current)
    {
        if (!CanRedo)
        {
            return current;
        }

        _undo.Push(current);
        return _redo.Pop();
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
