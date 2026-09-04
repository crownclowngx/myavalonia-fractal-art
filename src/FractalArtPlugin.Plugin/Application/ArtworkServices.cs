using System.Security.Cryptography;
using Avalonia.Media.Imaging;

namespace FractalArtPlugin.Application;

public interface IArtworkRenderPipeline
{
    Task<ArtworkRenderResult> RenderAsync(
        ArtworkDefinition artwork,
        RenderContext context,
        CancellationToken cancellationToken);
}

internal sealed class ArtworkRenderPipeline : IArtworkRenderPipeline
{
    private readonly IArtworkValidator _validator;
    private readonly IArtworkRenderabilityValidator _renderability;
    private readonly IArtworkGraphExecutor _executor;
    private readonly IScalarMaskConverter _maskConverter;
    private readonly ILayerRasterTransformer _transformer;
    private readonly ILayerCompositor _compositor;
    private readonly IMasterEffectRenderer _masterEffects;

    public ArtworkRenderPipeline(IArtworkValidator validator, IArtworkGraphExecutor executor)
        : this(
            validator,
            validator as IArtworkRenderabilityValidator ??
                throw new ArgumentException("作品验证器必须同时提供可渲染性检查。", nameof(validator)),
            executor,
            new ScalarMaskConverter(),
            new LayerRasterTransformer(),
            new LayerCompositor(),
            new MasterEffectRenderer())
    {
    }

    public ArtworkRenderPipeline(
        IArtworkValidator validator,
        IArtworkRenderabilityValidator renderability,
        IArtworkGraphExecutor executor,
        IScalarMaskConverter maskConverter,
        ILayerRasterTransformer transformer,
        ILayerCompositor compositor,
        IMasterEffectRenderer masterEffects)
    {
        _validator = validator;
        _renderability = renderability;
        _executor = executor;
        _maskConverter = maskConverter;
        _transformer = transformer;
        _compositor = compositor;
        _masterEffects = masterEffects;
    }

    public async Task<ArtworkRenderResult> RenderAsync(
        ArtworkDefinition artwork,
        RenderContext context,
        CancellationToken cancellationToken)
    {
        _validator.Validate(artwork);
        _renderability.EnsureRenderable(artwork);
        cancellationToken.ThrowIfCancellationRequested();

        // v1-v6 迁移后的作品必须保持旧版单层 RGBA 指纹。满足以下条件时，v7 的规范树与旧直通图
        // 完全等价，直接让既有执行器在真实背景上完成输出，也避免一次无意义的透明层往返取整。
        if (CanUseLegacyEquivalentSingleLayerPath(artwork, out var singleLayer))
        {
            var selected = artwork.SelectLayer(singleLayer.Id);
            return await _executor.ExecuteAsync(
                selected,
                RenderContext.ForLayer(selected, singleLayer, context),
                cancellationToken).ConfigureAwait(false);
        }

        var scalarFields = new Dictionary<string, ScalarField>(StringComparer.Ordinal);
        var cacheHits = new List<string>();
        var executed = new List<string>();
        var cacheableCount = 0;
        var current = _compositor.CreateBackground(context.Width, context.Height, artwork.Canvas.Background);

        // 图层列表以最上层在前保存和展示；像素合成必须从最底层向上执行。
        foreach (var layer in artwork.Layers.Reverse())
        {
            if (!layer.IsVisible)
            {
                continue;
            }

            var rendered = await RenderLayerAsync(
                artwork, layer, context, scalarFields, cacheHits, executed,
                count => cacheableCount += count, cancellationToken).ConfigureAwait(false);
            var mask = await ResolveMaskAsync(
                artwork, layer.Mask, context, scalarFields, cacheHits, executed,
                count => cacheableCount += count, cancellationToken).ConfigureAwait(false);
            current = _compositor.Composite(
                current, rendered, layer.Opacity, layer.BlendMode, mask, cancellationToken);
        }

        current = _masterEffects.Apply(current, artwork.MasterEffects, cancellationToken);
        return new ArtworkRenderResult(current,
            new ArtworkRenderExecutionSummary(cacheHits.AsReadOnly(), executed.AsReadOnly(), cacheableCount));
    }

    private static bool CanUseLegacyEquivalentSingleLayerPath(
        ArtworkDefinition artwork,
        out FractalLayerDefinition layer)
    {
        var candidate = artwork.Layers.Count == 1 ? artwork.Layers[0] as FractalLayerDefinition : null;
        layer = candidate!;
        return candidate is not null &&
               layer.IsVisible &&
               layer.Opacity == 1 &&
               layer.BlendMode == LayerBlendMode.Normal &&
               layer.Transform == LayerTransformDefinition.Identity &&
               layer.Mask is null &&
               artwork.MasterEffects.Effects.All(effect => !effect.IsEnabled);
    }

    private async Task<ImageSurface> RenderLayerAsync(
        ArtworkDefinition artwork,
        ArtworkLayerDefinition layer,
        RenderContext frame,
        Dictionary<string, ScalarField> scalarFields,
        List<string> cacheHits,
        List<string> executed,
        Action<int> addCacheable,
        CancellationToken cancellationToken)
    {
        if (layer is FractalLayerDefinition fractal)
        {
            var selected = artwork.SelectLayer(fractal.Id) with
            {
                Canvas = artwork.Canvas with { Background = new RgbaColor(0, 0, 0, 0) },
                MasterEffects = EffectChainDefinition.Empty
            };
            var layerContext = RenderContext.ForLayer(selected, fractal, frame);
            var result = await _executor.ExecuteAsync(selected, layerContext, cancellationToken).ConfigureAwait(false);
            MergeSummary(result.Execution, cacheHits, executed, addCacheable);
            return _transformer.Transform(result.Image, fractal.Transform, cancellationToken);
        }

        if (layer is LayerGroupDefinition group)
        {
            var groupSurface = _compositor.CreateTransparent(frame.Width, frame.Height);
            foreach (var child in group.Children.Reverse())
            {
                if (!child.IsVisible)
                {
                    continue;
                }

                var childImage = await RenderLayerAsync(
                    artwork, child, frame, scalarFields, cacheHits, executed, addCacheable, cancellationToken)
                    .ConfigureAwait(false);
                var childMask = await ResolveMaskAsync(
                    artwork, child.Mask, frame, scalarFields, cacheHits, executed, addCacheable, cancellationToken)
                    .ConfigureAwait(false);
                groupSurface = _compositor.Composite(
                    groupSurface, childImage, child.Opacity, child.BlendMode, childMask, cancellationToken);
            }

            return _transformer.Transform(groupSurface, group.Transform, cancellationToken);
        }

        throw new NotSupportedException($"图层 {layer.Name} 当前不可渲染。");
    }

    private async Task<Mask?> ResolveMaskAsync(
        ArtworkDefinition artwork,
        ScalarMaskDefinition? definition,
        RenderContext frame,
        Dictionary<string, ScalarField> scalarFields,
        List<string> cacheHits,
        List<string> executed,
        Action<int> addCacheable,
        CancellationToken cancellationToken)
    {
        if (definition is null)
        {
            return null;
        }

        var source = ArtworkLayerTree.FindFractal(artwork.Layers, definition.SourceLayerId) ??
            throw new InvalidDataException($"遮罩源 {definition.SourceLayerId} 不存在。");
        if (!scalarFields.TryGetValue(source.Id, out var field))
        {
            var selected = artwork.SelectLayer(source.Id) with { MasterEffects = EffectChainDefinition.Empty };
            var sourceContext = RenderContext.ForLayer(selected, source, frame);
            var result = await _executor.ExecuteScalarAsync(selected, sourceContext, cancellationToken).ConfigureAwait(false);
            field = result.Field;
            scalarFields.Add(source.Id, field);
            MergeSummary(result.Execution, cacheHits, executed, addCacheable);
        }

        var mask = _maskConverter.Convert(field, definition, cancellationToken);
        return _transformer.Transform(mask, source.Transform, cancellationToken);
    }

    private static void MergeSummary(
        ArtworkRenderExecutionSummary summary,
        List<string> cacheHits,
        List<string> executed,
        Action<int> addCacheable)
    {
        cacheHits.AddRange(summary.CacheHitNodeIds);
        executed.AddRange(summary.ExecutedNodeIds);
        addCacheable(summary.CacheableNodeCount);
    }
}

public interface IPngEncoder
{
    byte[] Encode(ImageSurface image, CancellationToken cancellationToken);
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

        var result = await pipeline.RenderAsync(
            artwork,
            RenderContext.ForExport(artwork),
            cancellationToken).ConfigureAwait(false);
        var png = encoder.Encode(result.Image, cancellationToken);
        await writer.WriteAsync(path, png, cancellationToken).ConfigureAwait(false);
    }
}

public interface IPreviewImageFactory
{
    Bitmap? Create(ImageSurface image, CancellationToken cancellationToken);
}

public interface IArtworkExportDialog
{
    Task<string?> PickPngPathAsync(string suggestedName, CancellationToken cancellationToken);
}

/// <summary>为测试和状态栏提供稳定指纹；它不参与缓存身份，也不替代作品的版本化配方。</summary>
internal static class RenderFingerprint
{
    public static string Create(ImageSurface image) =>
        Convert.ToHexString(SHA256.HashData(image.Pixels.Span)).ToLowerInvariant()[..16];
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
