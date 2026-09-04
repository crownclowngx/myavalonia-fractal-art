namespace FractalArtPlugin.Application;

internal abstract record ArtworkGraphValue(ArtworkGraphDataKind DataKind, long EstimatedByteSize);
internal sealed record ScalarFieldGraphValue(ScalarField Value)
    : ArtworkGraphValue(ArtworkGraphDataKind.ScalarField, Value.EstimatedByteSize);
internal sealed record PathGeometryGraphValue(PathGeometry Value)
    : ArtworkGraphValue(ArtworkGraphDataKind.PathGeometry, Value.EstimatedByteSize);
internal sealed record ImageSurfaceGraphValue(ImageSurface Value)
    : ArtworkGraphValue(ArtworkGraphDataKind.ImageSurface, Value.EstimatedByteSize);
internal sealed record MaskGraphValue(Mask Value)
    : ArtworkGraphValue(ArtworkGraphDataKind.Mask, Value.EstimatedByteSize);
internal sealed record PointCloudGraphValue(PointCloud Value)
    : ArtworkGraphValue(ArtworkGraphDataKind.PointCloud, Value.EstimatedByteSize);

internal interface IArtworkGraphNodeExecutor
{
    ArtworkGraphOperation Operation { get; }

    Task<ArtworkGraphValue> ExecuteAsync(
        ArtworkDefinition artwork,
        RenderContext context,
        IReadOnlyDictionary<string, ArtworkGraphValue> inputs,
        CancellationToken cancellationToken);
}

internal sealed class JuliaFieldNodeExecutor(IJuliaFieldGenerator generator) : IArtworkGraphNodeExecutor
{
    public ArtworkGraphOperation Operation => ArtworkGraphOperation.JuliaField;

    public async Task<ArtworkGraphValue> ExecuteAsync(
        ArtworkDefinition artwork,
        RenderContext context,
        IReadOnlyDictionary<string, ArtworkGraphValue> inputs,
        CancellationToken cancellationToken) =>
        new ScalarFieldGraphValue(await generator.GenerateAsync(artwork.Julia, context, cancellationToken).ConfigureAwait(false));
}

internal sealed class MandelbrotFieldNodeExecutor(IMandelbrotFieldGenerator generator) : IArtworkGraphNodeExecutor
{
    public ArtworkGraphOperation Operation => ArtworkGraphOperation.MandelbrotField;

    public async Task<ArtworkGraphValue> ExecuteAsync(
        ArtworkDefinition artwork,
        RenderContext context,
        IReadOnlyDictionary<string, ArtworkGraphValue> inputs,
        CancellationToken cancellationToken) =>
        new ScalarFieldGraphValue(await generator.GenerateAsync(artwork.Mandelbrot, context, cancellationToken).ConfigureAwait(false));
}

internal sealed class RecursiveTreePathNodeExecutor(IRecursiveTreePathGenerator generator) : IArtworkGraphNodeExecutor
{
    public ArtworkGraphOperation Operation => ArtworkGraphOperation.RecursiveTreePath;

    public Task<ArtworkGraphValue> ExecuteAsync(
        ArtworkDefinition artwork,
        RenderContext context,
        IReadOnlyDictionary<string, ArtworkGraphValue> inputs,
        CancellationToken cancellationToken) =>
        Task.FromResult<ArtworkGraphValue>(
            new PathGeometryGraphValue(generator.Generate(artwork.RecursiveTree, artwork.Seed, cancellationToken)));
}

/// <summary>L-System 展开和 Turtle 解释仍由两个窄领域服务承担，图节点只负责把它们组成一个路径输出。</summary>
internal sealed class LSystemPathNodeExecutor(
    ILSystemExpander expander,
    ITurtlePathInterpreter interpreter) : IArtworkGraphNodeExecutor
{
    public ArtworkGraphOperation Operation => ArtworkGraphOperation.LSystemPath;

    public Task<ArtworkGraphValue> ExecuteAsync(
        ArtworkDefinition artwork,
        RenderContext context,
        IReadOnlyDictionary<string, ArtworkGraphValue> inputs,
        CancellationToken cancellationToken)
    {
        var symbols = expander.Expand(artwork.LSystem, cancellationToken);
        return Task.FromResult<ArtworkGraphValue>(
            new PathGeometryGraphValue(interpreter.Interpret(artwork.LSystem, symbols, cancellationToken)));
    }
}

internal sealed class StrangeAttractorPointsNodeExecutor(IAttractorPointCloudGenerator generator)
    : IArtworkGraphNodeExecutor
{
    public ArtworkGraphOperation Operation => ArtworkGraphOperation.StrangeAttractorPoints;

    public async Task<ArtworkGraphValue> ExecuteAsync(
        ArtworkDefinition artwork,
        RenderContext context,
        IReadOnlyDictionary<string, ArtworkGraphValue> inputs,
        CancellationToken cancellationToken) =>
        new PointCloudGraphValue(await generator.GenerateAsync(
            artwork.StrangeAttractor, artwork.Seed, context, cancellationToken).ConfigureAwait(false));
}

internal sealed class PointDensityNodeExecutor(IPointDensityRenderer renderer) : IArtworkGraphNodeExecutor
{
    public ArtworkGraphOperation Operation => ArtworkGraphOperation.PointDensity;

    public async Task<ArtworkGraphValue> ExecuteAsync(
        ArtworkDefinition artwork,
        RenderContext context,
        IReadOnlyDictionary<string, ArtworkGraphValue> inputs,
        CancellationToken cancellationToken)
    {
        var cloud = ArtworkGraphNodeInput.GetInput<PointCloudGraphValue>(inputs, "source", Operation).Value;
        return new ScalarFieldGraphValue(await renderer.RenderAsync(
            cloud, artwork.StrangeAttractor, context, cancellationToken).ConfigureAwait(false));
    }
}

internal sealed class DensityGradientNodeExecutor(IDensityGradientMapper mapper) : IArtworkGraphNodeExecutor
{
    public ArtworkGraphOperation Operation => ArtworkGraphOperation.DensityGradient;

    public Task<ArtworkGraphValue> ExecuteAsync(
        ArtworkDefinition artwork,
        RenderContext context,
        IReadOnlyDictionary<string, ArtworkGraphValue> inputs,
        CancellationToken cancellationToken)
    {
        var field = ArtworkGraphNodeInput.GetInput<ScalarFieldGraphValue>(inputs, "source", Operation).Value;
        return Task.FromResult<ArtworkGraphValue>(
            new ImageSurfaceGraphValue(mapper.Map(field, artwork.Gradient, cancellationToken)));
    }
}

internal sealed class DensityGlowNodeExecutor(IDensityGlowRenderer renderer) : IArtworkGraphNodeExecutor
{
    public ArtworkGraphOperation Operation => ArtworkGraphOperation.DensityGlow;

    public Task<ArtworkGraphValue> ExecuteAsync(
        ArtworkDefinition artwork,
        RenderContext context,
        IReadOnlyDictionary<string, ArtworkGraphValue> inputs,
        CancellationToken cancellationToken)
    {
        var image = ArtworkGraphNodeInput.GetInput<ImageSurfaceGraphValue>(inputs, "image", Operation).Value;
        return Task.FromResult<ArtworkGraphValue>(
            new ImageSurfaceGraphValue(renderer.Apply(image, artwork.StrangeAttractor, cancellationToken)));
    }
}

internal sealed class ScalarGradientNodeExecutor(IGradientMapper mapper) : IArtworkGraphNodeExecutor
{
    public ArtworkGraphOperation Operation => ArtworkGraphOperation.ScalarGradient;

    public Task<ArtworkGraphValue> ExecuteAsync(
        ArtworkDefinition artwork,
        RenderContext context,
        IReadOnlyDictionary<string, ArtworkGraphValue> inputs,
        CancellationToken cancellationToken)
    {
        var field = ArtworkGraphNodeInput.GetInput<ScalarFieldGraphValue>(inputs, "source", Operation).Value;
        return Task.FromResult<ArtworkGraphValue>(
            new ImageSurfaceGraphValue(mapper.Map(field, artwork.Gradient, cancellationToken)));
    }
}

internal sealed class PathStrokeNodeExecutor(IPathStrokeRenderer renderer) : IArtworkGraphNodeExecutor
{
    public ArtworkGraphOperation Operation => ArtworkGraphOperation.PathStroke;

    public Task<ArtworkGraphValue> ExecuteAsync(
        ArtworkDefinition artwork,
        RenderContext context,
        IReadOnlyDictionary<string, ArtworkGraphValue> inputs,
        CancellationToken cancellationToken)
    {
        var geometry = ArtworkGraphNodeInput.GetInput<PathGeometryGraphValue>(inputs, "source", Operation).Value;
        var stroke = artwork.GeneratorKind == FractalGeneratorKind.LSystem
            ? new PathStrokeDefinition(artwork.LSystem.StrokeWidth, artwork.LSystem.StrokeWidthDecay, "l-system")
            : new PathStrokeDefinition(artwork.RecursiveTree.StrokeWidth, 0.82);
        var image = renderer.Render(
            geometry,
            stroke,
            artwork.Gradient,
            artwork.Canvas.Background,
            context,
            cancellationToken);
        return Task.FromResult<ArtworkGraphValue>(new ImageSurfaceGraphValue(image));
    }
}

/// <summary>
/// G0006 的效果链为空，因此只转交不可变图像引用。该节点建立正式边界但不伪造可见效果；
/// 后续具体效果必须返回新的 ImageSurface，不能修改输入缓存。
/// </summary>
internal sealed class EffectChainNodeExecutor : IArtworkGraphNodeExecutor
{
    public ArtworkGraphOperation Operation => ArtworkGraphOperation.EffectChain;

    public Task<ArtworkGraphValue> ExecuteAsync(
        ArtworkDefinition artwork,
        RenderContext context,
        IReadOnlyDictionary<string, ArtworkGraphValue> inputs,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ArtworkGraphValue>(
            ArtworkGraphNodeInput.GetInput<ImageSurfaceGraphValue>(inputs, "image", Operation));
    }
}

internal sealed class SingleLayerCompositionNodeExecutor : IArtworkGraphNodeExecutor
{
    public ArtworkGraphOperation Operation => ArtworkGraphOperation.SingleLayerComposition;

    public Task<ArtworkGraphValue> ExecuteAsync(
        ArtworkDefinition artwork,
        RenderContext context,
        IReadOnlyDictionary<string, ArtworkGraphValue> inputs,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ArtworkGraphValue>(
            ArtworkGraphNodeInput.GetInput<ImageSurfaceGraphValue>(inputs, "image", Operation));
    }
}

internal sealed class OutputNodeExecutor : IArtworkGraphNodeExecutor
{
    public ArtworkGraphOperation Operation => ArtworkGraphOperation.Output;

    public Task<ArtworkGraphValue> ExecuteAsync(
        ArtworkDefinition artwork,
        RenderContext context,
        IReadOnlyDictionary<string, ArtworkGraphValue> inputs,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ArtworkGraphValue>(
            ArtworkGraphNodeInput.GetInput<ImageSurfaceGraphValue>(inputs, "image", Operation));
    }
}

internal static class ArtworkGraphNodeInput
{
    public static T GetInput<T>(
        IReadOnlyDictionary<string, ArtworkGraphValue> inputs,
        string port,
        ArtworkGraphOperation operation)
        where T : ArtworkGraphValue
    {
        if (!inputs.TryGetValue(port, out var value) || value is not T typed)
        {
            throw new InvalidOperationException($"节点 {operation} 的输入端口 {port} 未提供预期数据。");
        }

        return typed;
    }
}
