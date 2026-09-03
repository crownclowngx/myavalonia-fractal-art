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
            artwork.RecursiveTree,
            artwork.Gradient,
            artwork.Canvas.Background,
            context,
            cancellationToken);
        return Task.FromResult(image);
    }
}
