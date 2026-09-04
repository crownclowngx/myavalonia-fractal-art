using FractalArtPlugin.Domain.Artwork;

namespace FractalArtPlugin.Domain.Rendering;

public interface IJuliaFieldGenerator
{
    Task<ScalarField> GenerateAsync(JuliaDefinition definition, RenderContext context, CancellationToken cancellationToken);
}

public interface IMandelbrotFieldGenerator
{
    Task<ScalarField> GenerateAsync(
        MandelbrotDefinition definition,
        RenderContext context,
        CancellationToken cancellationToken);
}

public interface IGradientMapper
{
    ImageSurface Map(ScalarField field, GradientDefinition gradient, CancellationToken cancellationToken);
}

public interface IRecursiveTreePathGenerator
{
    PathGeometry Generate(RecursiveTreeDefinition definition, long seed, CancellationToken cancellationToken);
}

public interface ILSystemValidator
{
    LSystemValidationResult Analyze(LSystemDefinition definition);
    void Validate(LSystemDefinition definition);
}

public interface ILSystemExpander
{
    string Expand(LSystemDefinition definition, CancellationToken cancellationToken);
}

public interface ITurtlePathInterpreter
{
    PathGeometry Interpret(LSystemDefinition definition, string symbols, CancellationToken cancellationToken);
}

public sealed record PathStrokeDefinition(
    double Width,
    double LevelDecay,
    string KernelName = "recursive-tree");

public interface IPathStrokeRenderer
{
    ImageSurface Render(
        PathGeometry geometry,
        PathStrokeDefinition stroke,
        GradientDefinition gradient,
        RgbaColor background,
        RenderContext context,
        CancellationToken cancellationToken);
}
