using FractalArtPlugin.Application;
using FractalArtPlugin.Domain.Fractals.RecursiveTree;
using Xunit;

namespace FractalArtPlugin.Tests;

public sealed class RecursivePathTests
{
    [Fact]
    public void 递归树先生成保留层级的矢量线段而不是像素()
    {
        var definition = ArtworkDefinition.CreateDefault().RecursiveTree with
        {
            Depth = 6,
            Branches = 2,
            Randomness = 0
        };

        var geometry = new RecursiveTreePathGenerator().Generate(definition, 42, CancellationToken.None);

        Assert.Equal(63, geometry.Segments.Count);
        Assert.Equal(5, geometry.MaximumLevel);
        Assert.Equal(1, geometry.Segments.Count(segment => segment.Level == 0));
        Assert.Equal(32, geometry.Segments.Count(segment => segment.Level == 5));
        Assert.All(geometry.Segments, segment =>
        {
            Assert.True(double.IsFinite(segment.Start.X));
            Assert.True(double.IsFinite(segment.End.Y));
        });
    }

    [Fact]
    public void 相同Seed逐段重现而不同Seed改变随机树()
    {
        var generator = new RecursiveTreePathGenerator();
        var definition = ArtworkDefinition.CreateDefault().RecursiveTree with { Randomness = 0.45 };

        var first = generator.Generate(definition, 20260903, CancellationToken.None);
        var repeated = generator.Generate(definition, 20260903, CancellationToken.None);
        var different = generator.Generate(definition, 20260904, CancellationToken.None);

        Assert.Equal(first.Segments, repeated.Segments);
        Assert.NotEqual(first.Segments, different.Segments);
    }

    [Fact]
    public void 深度与分叉组合超过五万线段时在领域边界被拒绝()
    {
        var artwork = ArtworkDefinition.CreateDefault() with
        {
            GeneratorKind = FractalGeneratorKind.RecursiveTree,
            Graph = ArtworkGraphFactory.Create(FractalGeneratorKind.RecursiveTree),
            RecursiveTree = ArtworkDefinition.CreateDefault().RecursiveTree with { Depth = 11, Branches = 3 }
        };

        var exception = Assert.Throws<InvalidDataException>(() => new ArtworkValidator().Validate(artwork));

        Assert.Contains("50,000", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 三分叉十层在预算内精确生成二万九千五百二十四段()
    {
        var definition = ArtworkDefinition.CreateDefault().RecursiveTree with
        {
            Depth = 10,
            Branches = 3,
            Randomness = 0
        };
        var artwork = ArtworkDefinition.CreateDefault() with
        {
            GeneratorKind = FractalGeneratorKind.RecursiveTree,
            Graph = ArtworkGraphFactory.Create(FractalGeneratorKind.RecursiveTree),
            RecursiveTree = definition
        };

        new ArtworkValidator().Validate(artwork);
        var geometry = new RecursiveTreePathGenerator().Generate(definition, artwork.Seed, CancellationToken.None);

        Assert.Equal(29_524, geometry.Segments.Count);
    }

    [Fact]
    public async Task 路径描边进入统一预览导出图像面并按层级产生颜色()
    {
        var pipeline = TestArtworkPipeline.Create();
        var artwork = ArtworkDefinition.CreateDefault() with
        {
            GeneratorKind = FractalGeneratorKind.RecursiveTree,
            Graph = ArtworkGraphFactory.Create(FractalGeneratorKind.RecursiveTree),
            Canvas = new CanvasDefinition(160, 120, new RgbaColor(1, 2, 3)),
            RecursiveTree = ArtworkDefinition.CreateDefault().RecursiveTree with
            {
                Depth = 5,
                Branches = 2,
                Randomness = 0,
                StrokeWidth = 8
            },
            Gradient = new GradientDefinition(new RgbaColor(180, 40, 20), new RgbaColor(20, 220, 80), new RgbaColor(0, 0, 0))
        };

        var image = (await pipeline.RenderAsync(
            artwork,
            RenderContext.ForExport(artwork),
            CancellationToken.None)).Image;

        Assert.Equal(160, image.Width);
        Assert.Equal(120, image.Height);
        Assert.Equal("recursive-tree", image.Diagnostics?.Kernel);
        var colors = Enumerable.Range(0, image.Width * image.Height)
            .Select(index => (R: image.Pixels[index * 4], G: image.Pixels[index * 4 + 1], B: image.Pixels[index * 4 + 2]))
            .Where(color => color != (1, 2, 3))
            .Distinct()
            .ToArray();
        Assert.NotEmpty(colors);
        Assert.Contains(colors, color => color.R > color.G);
        Assert.Contains(colors, color => color.G > color.R);
    }

    [Fact]
    public void 路径生成与描边都观察预先取消()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var artwork = ArtworkDefinition.CreateDefault().WithGeneratorKind(FractalGeneratorKind.RecursiveTree);
        var generator = new RecursiveTreePathGenerator();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            generator.Generate(artwork.RecursiveTree, artwork.Seed, cancellation.Token));

        var geometry = generator.Generate(artwork.RecursiveTree with { Depth = 2 }, artwork.Seed, CancellationToken.None);
        Assert.ThrowsAny<OperationCanceledException>(() => new PathStrokeRenderer().Render(
            geometry,
            new PathStrokeDefinition(artwork.RecursiveTree.StrokeWidth, 0.82),
            artwork.Gradient,
            artwork.Canvas.Background,
            new RenderContext(64, 64, RenderQuality.Draft, artwork.Seed,
                RenderContext.CurrentRendererVersion, NumericPrecision.Double, 16),
            cancellation.Token));
    }
}
