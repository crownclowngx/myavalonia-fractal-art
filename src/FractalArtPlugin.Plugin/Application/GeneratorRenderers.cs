namespace FractalArtPlugin.Application;

/// <summary>Julia 纵向链路适配器：标量场生成与渐变映射保持原有独立职责。</summary>
internal sealed class JuliaArtworkRenderer(
    IJuliaFieldGenerator generator,
    IGradientMapper gradientMapper) : IArtworkGeneratorRenderer
{
    public FractalGeneratorKind Kind => FractalGeneratorKind.Julia;

    public async Task<RgbaImage> RenderAsync(
        ArtworkDefinition artwork,
        RenderContext context,
        CancellationToken cancellationToken)
    {
        var field = await generator.GenerateAsync(artwork.Julia, context, cancellationToken).ConfigureAwait(false);
        return gradientMapper.Map(field, artwork.Gradient, cancellationToken);
    }
}

/// <summary>
/// 递归树纵向链路适配器：先生成可复用的 PathGeometry，再由描边器投影到图像面。
/// 两个阶段通过窄端口组合，未来增加 SVG 导出时可直接绕过描边器消费路径。
/// </summary>
internal sealed class RecursiveTreeArtworkRenderer(
    IRecursiveTreePathGenerator pathGenerator,
    IPathStrokeRenderer strokeRenderer) : IArtworkGeneratorRenderer
{
    public FractalGeneratorKind Kind => FractalGeneratorKind.RecursiveTree;

    public Task<RgbaImage> RenderAsync(
        ArtworkDefinition artwork,
        RenderContext context,
        CancellationToken cancellationToken)
    {
        var geometry = pathGenerator.Generate(artwork.RecursiveTree, artwork.Seed, cancellationToken);
        var image = strokeRenderer.Render(
            geometry,
            new PathStrokeDefinition(artwork.RecursiveTree.StrokeWidth, 0.82),
            artwork.Gradient,
            artwork.Canvas.Background,
            context,
            cancellationToken);
        return Task.FromResult(image);
    }
}

/// <summary>Mandelbrot 只替换标量场生成步骤，颜色、预览和导出继续复用统一管线。</summary>
internal sealed class MandelbrotArtworkRenderer(
    IMandelbrotFieldGenerator generator,
    IGradientMapper gradientMapper) : IArtworkGeneratorRenderer
{
    public FractalGeneratorKind Kind => FractalGeneratorKind.Mandelbrot;

    public async Task<RgbaImage> RenderAsync(
        ArtworkDefinition artwork,
        RenderContext context,
        CancellationToken cancellationToken)
    {
        var field = await generator.GenerateAsync(artwork.Mandelbrot, context, cancellationToken).ConfigureAwait(false);
        return gradientMapper.Map(field, artwork.Gradient, cancellationToken);
    }
}

/// <summary>L-System 依次完成受预算展开、Turtle 路径解释和共用描边，不让任一步骤读取 UI。</summary>
internal sealed class LSystemArtworkRenderer(
    ILSystemExpander expander,
    ITurtlePathInterpreter interpreter,
    IPathStrokeRenderer strokeRenderer) : IArtworkGeneratorRenderer
{
    public FractalGeneratorKind Kind => FractalGeneratorKind.LSystem;

    public Task<RgbaImage> RenderAsync(
        ArtworkDefinition artwork,
        RenderContext context,
        CancellationToken cancellationToken)
    {
        var symbols = expander.Expand(artwork.LSystem, cancellationToken);
        var geometry = interpreter.Interpret(artwork.LSystem, symbols, cancellationToken);
        var image = strokeRenderer.Render(
            geometry,
            new PathStrokeDefinition(artwork.LSystem.StrokeWidth, artwork.LSystem.StrokeWidthDecay, "l-system"),
            artwork.Gradient,
            artwork.Canvas.Background,
            context,
            cancellationToken);
        return Task.FromResult(image);
    }
}
