using FractalArtPlugin.Application;
using Xunit;

namespace FractalArtPlugin.Tests.Domain.Rendering;

public sealed class LSystemTests
{
    [Fact]
    public void Koch雪花的线段计数可在展开前精确预算()
    {
        var definition = Create("F--F--F", [new('F', "F+F--F+F")], 4, 60);

        var result = new LSystemValidator().Analyze(definition);

        Assert.True(result.IsValid);
        Assert.Equal(3 * Math.Pow(4, 4), result.EstimatedSegmentCount);
    }

    [Fact]
    public void 确定性展开严格执行每轮并行替换()
    {
        var definition = Create("F", [new('F', "F+F")], 2, 90);

        var expanded = new LSystemExpander(new LSystemValidator())
            .Expand(definition, CancellationToken.None);

        Assert.Equal("F+F+F+F", expanded);
    }

    [Fact]
    public void Turtle分支恢复位置方向长度并保留路径层级()
    {
        var definition = Create("F", [new('F', "F")], 0, 90);

        var geometry = new TurtlePathInterpreter().Interpret(
            definition,
            "F[+F]F",
            CancellationToken.None);

        Assert.Equal(3, geometry.Segments.Count);
        Assert.Equal([0, 1, 0], geometry.Segments.Select(segment => segment.Level));
        Assert.All(geometry.Segments, segment =>
        {
            Assert.InRange(segment.Start.X, 0, 1);
            Assert.InRange(segment.Start.Y, 0, 1);
            Assert.InRange(segment.End.X, 0, 1);
            Assert.InRange(segment.End.Y, 0, 1);
        });
    }

    [Fact]
    public void 非法符号括号和爆炸式规则在统一验证边界被拒绝()
    {
        var validator = new LSystemValidator();
        var illegalSymbol = Create("F", [new('F', "F*F")], 1, 90);
        var unclosedBranch = Create("F", [new('F', "F[F")], 1, 90);
        var overBudget = Create("F", [new('F', "FFFFFFFF")], 6, 90);
        var deepStackRule = new string('[', 110) + "F" + new string(']', 110);
        var overStack = Create("F", [new('F', deepStackRule)], 10, 90);

        var illegalResult = validator.Analyze(illegalSymbol);
        var branchResult = validator.Analyze(unclosedBranch);
        var budgetResult = validator.Analyze(overBudget);
        var stackResult = validator.Analyze(overStack);
        Assert.False(illegalResult.IsValid);
        Assert.Contains(illegalResult.Errors, error => error.Code == "symbol.unsupported" && error.Field.Contains("rules", StringComparison.Ordinal));
        Assert.Contains(branchResult.Errors, error => error.Code == "branch.unclosed");
        Assert.Contains(budgetResult.Errors, error => error.Code == "budget.symbols");
        Assert.Contains(stackResult.Errors, error => error.Code == "budget.stack");
    }

    [Fact]
    public void 展开和解释都观察预先取消()
    {
        var definition = Create("F", [new('F', "F+F")], 2, 90);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            new LSystemExpander(new LSystemValidator()).Expand(definition, cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() =>
            new TurtlePathInterpreter().Interpret(definition, "F+F", cancellation.Token));
    }

    [Fact]
    public void 预设目录提供三种Mandelbrot和五种经典LSystem起点()
    {
        var catalog = new ArtworkPresetCatalog();

        Assert.Equal(3, catalog.ArtworkPresets.Count(item => item.GeneratorKind == FractalGeneratorKind.Mandelbrot));
        Assert.Equal(5, catalog.ArtworkPresets.Count(item => item.GeneratorKind == FractalGeneratorKind.LSystem));
        Assert.All(
            catalog.ArtworkPresets.Where(item => item.GeneratorKind == FractalGeneratorKind.LSystem),
            preset => Assert.True(new LSystemValidator().Analyze(preset.LSystem!).IsValid));
    }

    [Theory]
    [InlineData("lsystem-koch", 768)]
    [InlineData("lsystem-dragon", 4096)]
    [InlineData("lsystem-sierpinski", 729)]
    [InlineData("lsystem-plant", 1488)]
    [InlineData("lsystem-hilbert", 1023)]
    public void 五个经典示例固定线段预算(string id, long expectedSegments)
    {
        var preset = new ArtworkPresetCatalog().ArtworkPresets.Single(item => item.Id == id);

        var analysis = new LSystemValidator().Analyze(preset.LSystem!);

        Assert.True(analysis.IsValid);
        Assert.Equal(expectedSegments, analysis.EstimatedSegmentCount);
    }

    [Fact]
    public async Task LSystem通过统一渲染策略进入RGBA图像面()
    {
        var artwork = new ArtworkPresetCatalog().ApplyArtworkPreset(
            ArtworkDefinition.CreateDefault() with
            {
                Canvas = new CanvasDefinition(96, 96, new RgbaColor(1, 2, 3))
            },
            "lsystem-koch");
        var validator = new ArtworkValidator();
        var lSystemValidator = new LSystemValidator();
        var pipeline = new ArtworkRenderPipeline(
            validator,
            [new LSystemArtworkRenderer(
                new LSystemExpander(lSystemValidator),
                new TurtlePathInterpreter(),
                new PathStrokeRenderer())]);

        var image = await pipeline.RenderAsync(
            artwork,
            RenderContext.ForExport(artwork),
            CancellationToken.None);

        Assert.Equal("l-system", image.Diagnostics?.Kernel);
        Assert.Contains(
            Enumerable.Range(0, image.Width * image.Height),
            index => image.Pixels[index * 4] != 1 || image.Pixels[index * 4 + 1] != 2 || image.Pixels[index * 4 + 2] != 3);
    }

    [Theory]
    [InlineData("lsystem-koch", "220170fe3299909a")]
    [InlineData("lsystem-dragon", "ccce4a1586993a9d")]
    [InlineData("lsystem-sierpinski", "80fa288263d6c5dd")]
    [InlineData("lsystem-plant", "c2bdbf0780751d3d")]
    [InlineData("lsystem-hilbert", "6358c8941996a699")]
    public async Task 五个经典示例保持固定RGBA指纹(string id, string expectedFingerprint)
    {
        var artwork = new ArtworkPresetCatalog().ApplyArtworkPreset(
            ArtworkDefinition.CreateDefault() with { Canvas = new CanvasDefinition(96, 96, new RgbaColor(1, 2, 3)) },
            id);
        var lSystemValidator = new LSystemValidator();
        var pipeline = new ArtworkRenderPipeline(
            new ArtworkValidator(lSystemValidator),
            [new LSystemArtworkRenderer(
                new LSystemExpander(lSystemValidator),
                new TurtlePathInterpreter(),
                new PathStrokeRenderer())]);

        var image = await pipeline.RenderAsync(artwork, RenderContext.ForExport(artwork), CancellationToken.None);

        Assert.Equal(expectedFingerprint, RenderFingerprint.Create(image));
    }

    private static LSystemDefinition Create(
        string axiom,
        IReadOnlyList<LSystemRuleDefinition> rules,
        int iterations,
        double angle) => new(axiom, rules, iterations, angle, 0, 0.1, 1, 2, 0.9);
}
