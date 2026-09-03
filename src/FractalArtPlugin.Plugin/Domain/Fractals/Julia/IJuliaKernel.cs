using FractalArtPlugin.Domain.Artwork;
using FractalArtPlugin.Domain.Rendering;

namespace FractalArtPlugin.Domain.Fractals.Julia;

internal interface IJuliaKernel
{
    string Name { get; }
    bool CanHandle(RenderContext context);
    ScalarField Generate(JuliaDefinition definition, RenderContext context, CancellationToken cancellationToken);
}
