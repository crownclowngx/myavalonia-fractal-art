using Xunit;

namespace FractalArtPlugin.Tests.Application;

public sealed class G0008RenderPipelineTests
{
    [Fact]
    public async Task 顶层与组内都按列表底到顶合成且背景只进入根级一次()
    {
        var executor = new SolidLayerExecutor(new Dictionary<string, byte[]>
        {
            ["top"] = [255, 0, 0, 255],
            ["middle"] = [0, 255, 0, 255],
            ["bottom"] = [0, 0, 255, 255]
        });
        var pipeline = CreatePipeline(executor);
        var top = Layer("top", FractalGeneratorKind.Julia);
        var middle = Layer("middle", FractalGeneratorKind.Mandelbrot);
        var bottom = Layer("bottom", FractalGeneratorKind.RecursiveTree);
        var artwork = Artwork([top, middle, bottom], "top");

        var root = await pipeline.RenderAsync(
            artwork, RenderContext.ForExport(artwork), CancellationToken.None);
        var group = new LayerGroupDefinition(
            "group-1", "分组 1", true, 1, LayerBlendMode.Normal,
            LayerTransformDefinition.Identity, null, [middle, bottom]);
        var groupedArtwork = Artwork([top with { IsVisible = false }, group], "group-1");
        var grouped = await pipeline.RenderAsync(
            groupedArtwork, RenderContext.ForExport(groupedArtwork), CancellationToken.None);

        Assert.Equal([255, 0, 0, 255], root.Image.Pixels.Span[..4].ToArray());
        Assert.Equal([0, 255, 0, 255], grouped.Image.Pixels.Span[..4].ToArray());
    }

    [Fact]
    public async Task 隐藏遮罩源只求值ScalarField且隐藏未引用分支完全跳过()
    {
        var executor = new SolidLayerExecutor(new Dictionary<string, byte[]>
        {
            ["target"] = [240, 20, 10, 255],
            ["source"] = [1, 2, 3, 255],
            ["unused"] = [4, 5, 6, 255]
        });
        var source = Layer("source", FractalGeneratorKind.Mandelbrot) with { IsVisible = false };
        var unused = Layer("unused", FractalGeneratorKind.Julia) with { IsVisible = false };
        var target = Layer("target", FractalGeneratorKind.RecursiveTree) with
        {
            Mask = new ScalarMaskDefinition(source.Id, 0.5, 0, false)
        };
        var artwork = Artwork([target, source, unused], target.Id);

        var result = await CreatePipeline(executor).RenderAsync(
            artwork, RenderContext.ForExport(artwork), CancellationToken.None);

        Assert.Equal([240, 20, 10, 255], result.Image.Pixels.Span[..4].ToArray());
        Assert.Equal(["target"], executor.FullRenderIds);
        Assert.Equal(["source"], executor.ScalarRenderIds);
    }

    [Fact]
    public async Task 组整体变换遮罩不透明度在组内合成之后应用()
    {
        var executor = new SolidLayerExecutor(new Dictionary<string, byte[]>
        {
            ["child"] = [240, 20, 10, 255],
            ["source"] = [1, 2, 3, 255]
        });
        var child = Layer("child", FractalGeneratorKind.RecursiveTree);
        var source = Layer("source", FractalGeneratorKind.Julia) with
        {
            IsVisible = false,
            Transform = LayerTransformDefinition.Identity with { PositionXPercent = 25 }
        };
        var group = new LayerGroupDefinition(
            "group-1", "组合", true, 0.5, LayerBlendMode.Normal,
            LayerTransformDefinition.Identity with { PositionXPercent = 25 },
            new ScalarMaskDefinition(source.Id, 0.5, 0, false), [child]);
        var artwork = Artwork([group, source], group.Id);

        var result = await CreatePipeline(executor).RenderAsync(
            artwork, RenderContext.ForExport(artwork), CancellationToken.None);

        Assert.Equal([0, 0, 0, 255], result.Image.Pixels.Span[..4].ToArray());
        Assert.Equal([120, 10, 5, 255], result.Image.Pixels.Span.Slice(16 * 4, 4).ToArray());
        Assert.Equal(["child"], executor.FullRenderIds);
        Assert.Equal(["source"], executor.ScalarRenderIds);
    }

    [Fact]
    public async Task 修改上层变换或MasterEffect不会重新计算未受影响生成节点()
    {
        var field = new CountingFieldGenerator();
        using var cache = new ArtworkGraphCache();
        var graphValidator = new ArtworkGraphValidator();
        var lSystem = new LSystemValidator();
        var graphExecutor = new ArtworkGraphExecutor(graphValidator, cache,
        [
            new JuliaFieldNodeExecutor(field),
            new MandelbrotFieldNodeExecutor(new MandelbrotFieldGenerator()),
            new RecursiveTreePathNodeExecutor(new RecursiveTreePathGenerator()),
            new LSystemPathNodeExecutor(new LSystemExpander(lSystem), new TurtlePathInterpreter()),
            new ScalarGradientNodeExecutor(new LinearGradientMapper()),
            new PathStrokeNodeExecutor(new PathStrokeRenderer()),
            new EffectChainNodeExecutor(),
            new SingleLayerCompositionNodeExecutor(),
            new OutputNodeExecutor()
        ]);
        var validator = new ArtworkValidator(lSystem, graphValidator);
        var pipeline = new ArtworkRenderPipeline(validator, graphExecutor);
        var top = Layer("top", FractalGeneratorKind.Julia);
        var bottom = Layer("bottom", FractalGeneratorKind.Julia);
        var artwork = Artwork([top, bottom], top.Id);
        var context = RenderContext.ForExport(artwork) with { MaxDegreeOfParallelism = 1 };

        await pipeline.RenderAsync(artwork, context, CancellationToken.None);
        await pipeline.RenderAsync(artwork with
        {
            Layers = [top with { Transform = top.Transform with { PositionXPercent = 10 } }, bottom]
        }, context, CancellationToken.None);
        await pipeline.RenderAsync(artwork with
        {
            MasterEffects = new EffectChainDefinition(1,
            [
                new ToneEffectDefinition(true, 0.1, 0, 1),
                new BloomEffectDefinition(false, 0.72, 2.4, 0.8)
            ])
        }, context, CancellationToken.None);

        Assert.Equal(2, field.CallCount);
    }

    [Fact]
    public async Task 未知能力在任何生成计算前由统一渲染边界阻止()
    {
        var executor = new SolidLayerExecutor(new Dictionary<string, byte[]>
        {
            ["layer-1"] = [1, 2, 3, 255]
        });
        var artwork = Artwork([Layer("layer-1", FractalGeneratorKind.Julia)], "layer-1") with
        {
            MasterEffects = new EffectChainDefinition(1,
            [new UnavailableEffectDefinition("future.effect", 9, true, "{\"value\":1}")])
        };

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => CreatePipeline(executor).RenderAsync(
            artwork, RenderContext.ForExport(artwork), CancellationToken.None));

        Assert.Contains("future.effect", error.Message, StringComparison.Ordinal);
        Assert.Empty(executor.FullRenderIds);
        Assert.Empty(executor.ScalarRenderIds);
    }

    private static ArtworkRenderPipeline CreatePipeline(IArtworkGraphExecutor executor)
    {
        var validator = new ArtworkValidator();
        return new ArtworkRenderPipeline(
            validator, validator, executor, new ScalarMaskConverter(),
            new LayerRasterTransformer(), new LayerCompositor(), new MasterEffectRenderer());
    }

    private static ArtworkDefinition Artwork(IReadOnlyList<ArtworkLayerDefinition> layers, string selectedId) =>
        new(
            ArtworkDefinition.CurrentFormatVersion,
            new CanvasDefinition(64, 64, new RgbaColor(0, 0, 0)),
            new ArtworkPresentationDefinition("图层", false, selectedId),
            layers,
            EffectChainDefinition.CreateDefaultMaster());

    private static FractalLayerDefinition Layer(string id, FractalGeneratorKind kind) =>
        ArtworkDefinition.CreateDefaultLayer(id, kind) with { Name = id };

    private sealed class SolidLayerExecutor(IReadOnlyDictionary<string, byte[]> colors) : IArtworkGraphExecutor
    {
        public List<string> FullRenderIds { get; } = [];
        public List<string> ScalarRenderIds { get; } = [];

        public Task<ArtworkRenderResult> ExecuteAsync(
            ArtworkDefinition artwork,
            RenderContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = artwork.SelectedFractalLayer.Id;
            FullRenderIds.Add(id);
            var color = colors[id];
            var pixels = new byte[context.Width * context.Height * 4];
            for (var offset = 0; offset < pixels.Length; offset += 4)
            {
                color.CopyTo(pixels, offset);
            }

            return Task.FromResult(new ArtworkRenderResult(
                new ImageSurface(context.Width, context.Height, pixels),
                new ArtworkRenderExecutionSummary([], [$"{id}-output"], 0)));
        }

        public Task<(ScalarField Field, ArtworkRenderExecutionSummary Execution)> ExecuteScalarAsync(
            ArtworkDefinition artwork,
            RenderContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = artwork.SelectedFractalLayer.Id;
            ScalarRenderIds.Add(id);
            var length = context.Width * context.Height;
            return Task.FromResult((
                new ScalarField(context.Width, context.Height,
                    Enumerable.Repeat(1f, length).ToArray(), Enumerable.Repeat(true, length).ToArray()),
                new ArtworkRenderExecutionSummary([], [$"{id}-generator"], 1)));
        }
    }

    private sealed class CountingFieldGenerator : IJuliaFieldGenerator
    {
        public int CallCount { get; private set; }

        public Task<ScalarField> GenerateAsync(
            JuliaDefinition definition,
            RenderContext context,
            CancellationToken cancellationToken)
        {
            CallCount++;
            var length = context.Width * context.Height;
            return Task.FromResult(new ScalarField(
                context.Width, context.Height, new float[length], Enumerable.Repeat(true, length).ToArray()));
        }
    }
}
