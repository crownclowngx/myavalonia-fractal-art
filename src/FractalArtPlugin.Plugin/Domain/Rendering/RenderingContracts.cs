using FractalArtPlugin.Domain.Artwork;

namespace FractalArtPlugin.Domain.Rendering;

public interface IJuliaFieldGenerator
{
    Task<ScalarField> GenerateAsync(JuliaDefinition definition, RenderContext context, CancellationToken cancellationToken);
}

public interface IGradientMapper
{
    RgbaImage Map(ScalarField field, GradientDefinition gradient, CancellationToken cancellationToken);
}
