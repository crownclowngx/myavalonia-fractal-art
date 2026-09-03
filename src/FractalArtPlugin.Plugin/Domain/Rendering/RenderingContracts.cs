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
    RgbaImage Map(ScalarField field, GradientDefinition gradient, CancellationToken cancellationToken);
}

/// <summary>
/// 一种作品生成器的完整渲染策略。应用管线只按稳定类型选择策略，不了解标量场或路径的内部步骤，
/// 因而增加第三类数据形态时无需继续扩大 Document 或导出器的职责。
/// </summary>
public interface IArtworkGeneratorRenderer
{
    FractalGeneratorKind Kind { get; }
    Task<RgbaImage> RenderAsync(ArtworkDefinition artwork, RenderContext context, CancellationToken cancellationToken);
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
    RgbaImage Render(
        PathGeometry geometry,
        PathStrokeDefinition stroke,
        GradientDefinition gradient,
        RgbaColor background,
        RenderContext context,
        CancellationToken cancellationToken);
}
