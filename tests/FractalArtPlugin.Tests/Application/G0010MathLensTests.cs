using FractalArtPlugin.Features.Artwork;
using Xunit;

namespace FractalArtPlugin.Tests.Application;

public sealed class G0010MathLensTests
{
    [Fact]
    public async Task Julia双精度轨迹与生产标量场同像素一致()
    {
        var artwork = ArtworkDefinition.CreateDefault();
        var context = RenderContext.ForPreview(artwork);
        var centerX = context.Width / 2;
        var centerY = context.Height / 2;
        var definition = artwork.Julia;
        var scale = ArbitraryDecimal.Parse(definition.Scale).ToDouble();
        var step = scale / Math.Max(1, context.Height - 1);
        var real = ArbitraryDecimal.Parse(definition.CenterX).ToDouble() -
            (context.Width - 1) * step / 2 + centerX * step;
        var imaginary = ArbitraryDecimal.Parse(definition.CenterY).ToDouble() -
            scale / 2 + centerY * step;
        var trace = new List<EscapeOrbitPoint>();

        var sample = EscapeOrbitMath.ComputeDouble(
            real,
            imaginary,
            ArbitraryDecimal.Parse(definition.ConstantReal).ToDouble(),
            ArbitraryDecimal.Parse(definition.ConstantImaginary).ToDouble(),
            definition.MaxIterations,
            context.CancellationCheckInterval,
            CancellationToken.None,
            trace).ToScalar(definition.MaxIterations);
        var field = await new JuliaFieldGenerator().GenerateAsync(definition, context, CancellationToken.None);
        var index = centerY * context.Width + centerX;

        Assert.NotEmpty(trace);
        Assert.Equal(field.Escaped[index], sample.Escaped);
        Assert.Equal(field.Values[index], sample.Value);
    }

    [Fact]
    public async Task Julia任意精度轨迹与权威内核同像素一致()
    {
        var artwork = ArtworkDefinition.CreateDefault() with
        {
            Julia = ArtworkDefinition.CreateDefault().Julia with
            {
                Scale = "1e-20",
                ForceHighPrecision = true,
                PrecisionDigits = 96
            }
        };
        var context = RenderContext.ForPreview(artwork) with
        {
            KernelPreference = JuliaKernelPreference.ReferenceArbitrary
        };
        var fixedPoint = BinaryFixedPoint.ForDecimalDigits(context.EffectivePrecisionDigits);
        var frame = ArbitraryJuliaKernel.FrameCoordinates.Create(artwork.Julia, context, fixedPoint);
        var x = context.Width / 2;
        var y = context.Height / 2;
        var trace = new List<EscapeOrbitPoint>();
        var sample = EscapeOrbitMath.ComputeFixed(
            fixedPoint,
            frame.Left + x * frame.PixelStep,
            frame.Top + y * frame.PixelStep,
            frame.ConstantReal,
            frame.ConstantImaginary,
            artwork.Julia.MaxIterations,
            context.CancellationCheckInterval,
            CancellationToken.None,
            trace).ToScalar(artwork.Julia.MaxIterations);
        var field = await new JuliaFieldGenerator().GenerateAsync(artwork.Julia, context, CancellationToken.None);

        Assert.NotEmpty(trace);
        Assert.Equal(field.Escaped[y * context.Width + x], sample.Escaped);
        Assert.Equal(field.Values[y * context.Width + x], sample.Value);
    }

    [Fact]
    public async Task 五种生成器按三类策略分析且展示帧有界()
    {
        var service = CreateService();
        foreach (var kind in Enum.GetValues<FractalGeneratorKind>())
        {
            var artwork = ArtworkDefinition.CreateDefault().WithGeneratorKind(kind);
            var analysis = await service.AnalyzeAsync(
                artwork, artwork.SelectedFractalLayer.Id, MathLensSelection.Center, CancellationToken.None);

            Assert.NotEqual(MathLensKind.Information, analysis.Kind);
            Assert.NotEmpty(analysis.Formula);
            Assert.NotEmpty(analysis.Frames);
            Assert.True(analysis.Frames.Count <= 240);
        }
    }

    [Fact]
    public async Task 递归树最终帧逐段覆盖生产路径()
    {
        var artwork = ArtworkDefinition.CreateDefault().WithGeneratorKind(FractalGeneratorKind.RecursiveTree);
        var layer = artwork.SelectedFractalLayer;
        var expected = new RecursiveTreePathGenerator().Generate(layer.RecursiveTree, layer.Seed, CancellationToken.None);
        var analysis = await CreateService().AnalyzeAsync(
            artwork, layer.Id, null, CancellationToken.None);

        Assert.Equal(expected.Segments.Count, analysis.Frames[^1].VisibleSegmentCount);
        Assert.Equal(expected.MaximumLevel + 1, analysis.Frames.Count);
    }

    [Fact]
    public async Task LSystem替换和动作批次最终覆盖全部生产线段()
    {
        var artwork = ArtworkDefinition.CreateDefault().WithGeneratorKind(FractalGeneratorKind.LSystem);
        var layer = artwork.SelectedFractalLayer;
        var validator = new LSystemValidator();
        var expander = new LSystemExpander(validator);
        var symbols = expander.Expand(layer.LSystem, CancellationToken.None);
        var expected = new TurtlePathInterpreter().Interpret(layer.LSystem, symbols, CancellationToken.None);
        var analysis = await CreateService().AnalyzeAsync(artwork, layer.Id, null, CancellationToken.None);

        Assert.True(analysis.Frames.Count <= layer.LSystem.Iterations + 1 + 120);
        Assert.Equal(expected.Segments.Count, analysis.Frames[^1].VisibleSegmentCount);
        Assert.Contains("当前符号", analysis.Frames[^1].Annotation, StringComparison.Ordinal);
    }

    [Fact]
    public void 图层正逆投影往返保持坐标()
    {
        var transform = new LayerTransformDefinition(12, -8, 73, 31, 35, 62);
        var forward = LayerCoordinateProjection.ForwardMap(123.5, 234.5, 640, 480, transform);
        var inverse = LayerCoordinateProjection.InverseMap(forward.X, forward.Y, 640, 480, transform);

        Assert.Equal(123.5, inverse.X, 10);
        Assert.Equal(234.5, inverse.Y, 10);
    }

    [Fact]
    public void 点云透镜与密度渲染共用同一取景投影()
    {
        var cloud = new PointCloud([new PointSample(-2, -1), new PointSample(2, 3)]);
        var projection = PointCloudProjection.Create(cloud, 101, 81);
        var center = projection.Map(new PointSample(0, 1));

        Assert.Equal(50, center.X, 10);
        Assert.Equal(40, center.Y, 10);
    }

    [Fact]
    public void Uniform图片投影拒绝信箱边并正确归一化中心点()
    {
        var projection = UniformImageProjection.Create(1000, 500, 400, 400)!.Value;

        Assert.Null(projection.TryNormalize(100, 250));
        Assert.Equal(new MathLensSelection(0.5, 0.5), projection.TryNormalize(500, 250));
        Assert.Equal(250, projection.X, 10);
        Assert.Equal(500, projection.Width, 10);
    }

    [Fact]
    public async Task 隐藏层仍可解释并明确标注隐藏状态()
    {
        var artwork = ArtworkDefinition.CreateDefault();
        var hidden = artwork.SelectedFractalLayer with { IsVisible = false };
        artwork = artwork with { Layers = [hidden] };

        var analysis = await CreateService().AnalyzeAsync(
            artwork, hidden.Id, MathLensSelection.Center, CancellationToken.None);

        Assert.Contains("当前层已隐藏", analysis.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 分组选择返回提示且不会沿用旧层分析()
    {
        var layer = ArtworkDefinition.CreateDefault().SelectedFractalLayer;
        var group = new LayerGroupDefinition(
            "group-1", "组", true, 1, LayerBlendMode.Normal,
            LayerTransformDefinition.Identity, null, [layer]);
        var artwork = new ArtworkDefinition(
            ArtworkDefinition.CurrentFormatVersion,
            ArtworkDefinition.CreateDefault().Canvas,
            new ArtworkPresentationDefinition("图层", false, group.Id),
            [group],
            EffectChainDefinition.CreateDefaultMaster());

        var analysis = await CreateService().AnalyzeAsync(
            artwork, group.Id, null, CancellationToken.None);

        Assert.Equal(MathLensKind.Information, analysis.Kind);
        Assert.Contains("请选择分形层", analysis.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 预先取消不会产生任何分析结果()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var artwork = ArtworkDefinition.CreateDefault();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateService().AnalyzeAsync(
            artwork, artwork.SelectedFractalLayer.Id, null, cancellation.Token));
    }

    [Fact]
    public async Task 会话播放暂停单步和取消均保持分析边界()
    {
        var clock = new ManualClock();
        using var session = new MathLensSession(new FixedLensService(CreateAnalysis("layer-1", "A")), clock);
        var artwork = ArtworkDefinition.CreateDefault();
        await session.OpenAsync(artwork, artwork.SelectedFractalLayer.Id);

        session.Next();
        Assert.Equal(1, session.FrameIndex);
        session.Play();
        clock.Tick();
        await WaitUntilAsync(() => session.FrameIndex == 2);
        Assert.False(session.IsPlaying);

        session.Reset();
        session.Cancel();
        Assert.Equal(0, session.FrameIndex);
        Assert.True(session.IsOpen);
    }

    [Fact]
    public async Task 迟到分析不能覆盖新图层结果()
    {
        var service = new SequencedLensService();
        using var session = new MathLensSession(service);
        var artwork = ArtworkDefinition.CreateDefault();
        var first = session.OpenAsync(artwork, "layer-1");
        await service.FirstStarted.Task;
        var second = session.RefreshAsync(artwork, "layer-2", preserveSelection: false);
        await service.SecondStarted.Task;
        service.CompleteSecond(CreateAnalysis("layer-2", "新结果"));
        await second;
        service.CompleteFirst(CreateAnalysis("layer-1", "旧结果"));
        await first;

        Assert.Equal("layer-2", session.Analysis?.LayerId);
        Assert.Equal("新结果", session.Analysis?.Title);
    }

    private static IMathLensService CreateService()
    {
        var validator = new LSystemValidator();
        var kernels = new IAttractorFormulaKernel[] { new CliffordAttractorKernel(), new DeJongAttractorKernel() };
        return new MathLensService([
            new EscapeTimeMathLensProvider(new LinearGradientMapper()),
            new PathMathLensProvider(
                new RecursiveTreePathGenerator(),
                new LSystemExpander(validator),
                new TurtlePathInterpreter()),
            new AttractorMathLensProvider(new StrangeAttractorPointGenerator(kernels), kernels)
        ]);
    }

    private static MathLensAnalysis CreateAnalysis(string layerId, string title)
    {
        var frames = Enumerable.Range(0, 3).Select(index => new MathLensFrame(
            $"帧 {index}", "说明", index, 2, [], 0, [], 0)).ToArray();
        return new MathLensAnalysis(layerId, MathLensKind.Information, title, "公式", "解释", frames);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FixedLensService(MathLensAnalysis analysis) : IMathLensService
    {
        public Task<MathLensAnalysis> AnalyzeAsync(
            ArtworkDefinition artwork,
            string selectedLayerId,
            MathLensSelection? selection,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(analysis);
        }
    }

    private sealed class ManualClock : IMathLensPlaybackClock
    {
        private readonly SemaphoreSlim _ticks = new(0);
        public void Tick() => _ticks.Release();
        public ValueTask WaitForNextFrameAsync(CancellationToken cancellationToken) =>
            new(_ticks.WaitAsync(cancellationToken));
    }

    private sealed class SequencedLensService : IMathLensService
    {
        private readonly TaskCompletionSource<MathLensAnalysis> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<MathLensAnalysis> _second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<MathLensAnalysis> AnalyzeAsync(
            ArtworkDefinition artwork,
            string selectedLayerId,
            MathLensSelection? selection,
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                FirstStarted.TrySetResult();
                return _first.Task;
            }

            SecondStarted.TrySetResult();
            return _second.Task;
        }

        public void CompleteFirst(MathLensAnalysis analysis) => _first.TrySetResult(analysis);
        public void CompleteSecond(MathLensAnalysis analysis) => _second.TrySetResult(analysis);
    }
}
