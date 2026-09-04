using Xunit;

namespace FractalArtPlugin.Tests.Application;

public sealed class ArtworkGraphCacheTests
{
    [Fact]
    public async Task 相同渲染完整命中且修改渐变只重算着色下游()
    {
        var field = new CountingJuliaGenerator();
        var gradient = new CountingGradientMapper();
        using var cache = new ArtworkGraphCache();
        var pipeline = CreatePipeline(cache, field, gradient);
        var artwork = ArtworkDefinition.CreateDefault() with
        {
            Canvas = new CanvasDefinition(64, 64, ArtworkDefinition.CreateDefault().Canvas.Background),
            Julia = ArtworkDefinition.CreateDefault().Julia with { MaxIterations = 32 }
        };
        var context = RenderContext.ForExport(artwork) with { MaxDegreeOfParallelism = 1 };

        var cold = await pipeline.RenderAsync(artwork, context, CancellationToken.None);
        var warm = await pipeline.RenderAsync(artwork, context, CancellationToken.None);
        var recolored = await pipeline.RenderAsync(
            artwork with
            {
                Gradient = artwork.Gradient with { End = new RgbaColor(1, 2, 3) }
            },
            context,
            CancellationToken.None);

        Assert.False(cold.Execution.FullyFromCache);
        Assert.True(warm.Execution.FullyFromCache);
        Assert.False(recolored.Execution.FullyFromCache);
        Assert.Contains("generator", recolored.Execution.CacheHitNodeIds);
        Assert.Equal(1, field.CallCount);
        Assert.Equal(2, gradient.CallCount);
    }

    [Fact]
    public async Task 生成参数尺寸质量和版本进入相关节点缓存键()
    {
        var field = new CountingJuliaGenerator();
        var gradient = new CountingGradientMapper();
        using var cache = new ArtworkGraphCache();
        var pipeline = CreatePipeline(cache, field, gradient);
        var artwork = ArtworkDefinition.CreateDefault() with
        {
            Canvas = new CanvasDefinition(64, 64, ArtworkDefinition.CreateDefault().Canvas.Background),
            Julia = ArtworkDefinition.CreateDefault().Julia with { MaxIterations = 32 }
        };
        var original = RenderContext.ForExport(artwork) with { MaxDegreeOfParallelism = 1 };

        await pipeline.RenderAsync(artwork, original, CancellationToken.None);
        await pipeline.RenderAsync(
            artwork with { Julia = artwork.Julia with { ConstantReal = "-0.7" } },
            original,
            CancellationToken.None);
        await pipeline.RenderAsync(artwork, original with { Width = 65 }, CancellationToken.None);
        await pipeline.RenderAsync(artwork, original with { Quality = RenderQuality.Draft }, CancellationToken.None);
        await pipeline.RenderAsync(artwork, original with { RendererVersion = original.RendererVersion + 1 }, CancellationToken.None);

        Assert.Equal(5, field.CallCount);
        Assert.Equal(5, gradient.CallCount);
    }

    [Fact]
    public async Task 路径几何跨尺寸复用而描边按尺寸重算()
    {
        var paths = new CountingTreeGenerator();
        var strokes = new CountingPathRenderer();
        using var cache = new ArtworkGraphCache();
        var pipeline = CreatePipeline(cache, tree: paths, stroke: strokes);
        var artwork = ArtworkDefinition.CreateDefault()
            .WithGeneratorKind(FractalGeneratorKind.RecursiveTree) with
        {
            Canvas = new CanvasDefinition(64, 64, new RgbaColor(1, 2, 3))
        };
        var first = RenderContext.ForExport(artwork);

        await pipeline.RenderAsync(artwork, first, CancellationToken.None);
        var second = await pipeline.RenderAsync(artwork, first with { Width = 80, Height = 80 }, CancellationToken.None);
        await pipeline.RenderAsync(
            artwork with
            {
                RecursiveTree = artwork.RecursiveTree with { StrokeWidth = artwork.RecursiveTree.StrokeWidth + 1 }
            },
            first,
            CancellationToken.None);
        await pipeline.RenderAsync(artwork with { Seed = artwork.Seed + 1 }, first, CancellationToken.None);

        Assert.Equal(2, paths.CallCount);
        Assert.Equal(4, strokes.CallCount);
        Assert.Contains("generator", second.Execution.CacheHitNodeIds);
    }

    [Fact]
    public void Lru同时执行条目和字节预算且超大项不入缓存()
    {
        using var cache = new ArtworkGraphCache(maximumBytes: 8, maximumEntries: 2);
        var first = new ArtworkNodeCacheKey("first");
        var second = new ArtworkNodeCacheKey("second");
        var third = new ArtworkNodeCacheKey("third");
        cache.Set(first, new ImageSurfaceGraphValue(new ImageSurface(1, 1, [1, 2, 3, 4])));
        cache.Set(second, new ImageSurfaceGraphValue(new ImageSurface(1, 1, [5, 6, 7, 8])));
        Assert.True(cache.TryGet(first, out _));

        cache.Set(third, new ImageSurfaceGraphValue(new ImageSurface(1, 1, [9, 10, 11, 12])));
        cache.Set(new ArtworkNodeCacheKey("oversized"),
            new ImageSurfaceGraphValue(new ImageSurface(2, 2, new byte[16])));

        Assert.True(cache.TryGet(first, out _));
        Assert.False(cache.TryGet(second, out _));
        Assert.True(cache.TryGet(third, out _));
        Assert.Equal(2, cache.Count);
        Assert.Equal(8, cache.CurrentBytes);
    }

    [Fact]
    public async Task 取消和异常不写入缓存且错误包含节点上下文()
    {
        using var cache = new ArtworkGraphCache();
        var failing = new FailingJuliaGenerator();
        var pipeline = CreatePipeline(cache, failing);
        var artwork = ArtworkDefinition.CreateDefault() with
        {
            Canvas = new CanvasDefinition(64, 64, ArtworkDefinition.CreateDefault().Canvas.Background)
        };
        var context = RenderContext.ForExport(artwork);

        var error = await Assert.ThrowsAsync<ArtworkGraphExecutionException>(() =>
            pipeline.RenderAsync(artwork, context, CancellationToken.None));
        Assert.Equal("generator", error.NodeId);
        Assert.Contains("JuliaField", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, cache.Count);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pipeline.RenderAsync(artwork, context, cancellation.Token));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task 缓存在并发读写下保持预算与内容一致()
    {
        using var cache = new ArtworkGraphCache(maximumBytes: 128, maximumEntries: 16);
        await Parallel.ForEachAsync(Enumerable.Range(0, 200), async (index, token) =>
        {
            var key = new ArtworkNodeCacheKey($"key-{index % 32}");
            cache.Set(key, new ImageSurfaceGraphValue(new ImageSurface(1, 1, [(byte)index, 0, 0, 255])));
            cache.TryGet(key, out _);
            await Task.Yield();
        });

        Assert.InRange(cache.Count, 1, 16);
        Assert.InRange(cache.CurrentBytes, 1, 128);
    }

    private static IArtworkRenderPipeline CreatePipeline(
        IArtworkGraphCache cache,
        IJuliaFieldGenerator? julia = null,
        IGradientMapper? gradient = null,
        IRecursiveTreePathGenerator? tree = null,
        IPathStrokeRenderer? stroke = null)
    {
        var graphValidator = new ArtworkGraphValidator();
        var lSystem = new LSystemValidator();
        IArtworkGraphNodeExecutor[] executors =
        [
            new JuliaFieldNodeExecutor(julia ?? new JuliaFieldGenerator()),
            new MandelbrotFieldNodeExecutor(new MandelbrotFieldGenerator()),
            new RecursiveTreePathNodeExecutor(tree ?? new RecursiveTreePathGenerator()),
            new LSystemPathNodeExecutor(new LSystemExpander(lSystem), new TurtlePathInterpreter()),
            new ScalarGradientNodeExecutor(gradient ?? new LinearGradientMapper()),
            new PathStrokeNodeExecutor(stroke ?? new PathStrokeRenderer()),
            new EffectChainNodeExecutor(),
            new SingleLayerCompositionNodeExecutor(),
            new OutputNodeExecutor()
        ];
        var executor = new ArtworkGraphExecutor(graphValidator, cache, executors);
        return new ArtworkRenderPipeline(new ArtworkValidator(lSystem, graphValidator), executor);
    }

    private sealed class CountingJuliaGenerator : IJuliaFieldGenerator
    {
        public int CallCount { get; private set; }

        public Task<ScalarField> GenerateAsync(
            JuliaDefinition definition,
            RenderContext context,
            CancellationToken cancellationToken)
        {
            CallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            var length = checked(context.Width * context.Height);
            return Task.FromResult(new ScalarField(
                context.Width,
                context.Height,
                new float[length],
                Enumerable.Repeat(true, length).ToArray()));
        }
    }

    private sealed class FailingJuliaGenerator : IJuliaFieldGenerator
    {
        public Task<ScalarField> GenerateAsync(
            JuliaDefinition definition,
            RenderContext context,
            CancellationToken cancellationToken) =>
            Task.FromException<ScalarField>(new InvalidOperationException("测试故障"));
    }

    private sealed class CountingGradientMapper : IGradientMapper
    {
        public int CallCount { get; private set; }

        public ImageSurface Map(ScalarField field, GradientDefinition gradient, CancellationToken cancellationToken)
        {
            CallCount++;
            return new LinearGradientMapper().Map(field, gradient, cancellationToken);
        }
    }

    private sealed class CountingTreeGenerator : IRecursiveTreePathGenerator
    {
        public int CallCount { get; private set; }

        public PathGeometry Generate(
            RecursiveTreeDefinition definition,
            long seed,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return new RecursiveTreePathGenerator().Generate(definition, seed, cancellationToken);
        }
    }

    private sealed class CountingPathRenderer : IPathStrokeRenderer
    {
        public int CallCount { get; private set; }

        public ImageSurface Render(
            PathGeometry geometry,
            PathStrokeDefinition stroke,
            GradientDefinition gradient,
            RgbaColor background,
            RenderContext context,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return new PathStrokeRenderer().Render(geometry, stroke, gradient, background, context, cancellationToken);
        }
    }
}
