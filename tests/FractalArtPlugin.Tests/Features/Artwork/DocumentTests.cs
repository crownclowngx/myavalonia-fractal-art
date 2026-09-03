using Avalonia.Media.Imaging;
using FractalArtPlugin.Application;
using FractalArtPlugin.Features.Artwork;
using MyAvaloniaManagement.PluginSdk;
using Xunit;

namespace FractalArtPlugin.Tests;

public sealed class DocumentTests
{
    [Fact]
    public async Task 新建修改保存确认和旧修订确认遵守Dirty语义()
    {
        using var fixture = new DocumentFixture();
        var document = fixture.CreateDocument();
        var dirtyTransitions = 0;
        document.IsDirtyChanged += (_, _) => dirtyTransitions++;
        await document.InitializeAsync(new NewDocumentActivation("测试作品"), CancellationToken.None);

        Assert.False(document.IsDirty);
        document.CanvasWidth = 1600;
        Assert.True(document.IsDirty);
        var oldSnapshot = await document.CaptureSaveSnapshotAsync(CancellationToken.None);
        document.CanvasHeight = 900;

        document.AcceptChanges(oldSnapshot.Revision);
        Assert.True(document.IsDirty);
        var latest = await document.CaptureSaveSnapshotAsync(CancellationToken.None);
        document.AcceptChanges(latest.Revision);

        Assert.False(document.IsDirty);
        Assert.Equal(2, dirtyTransitions);
    }

    [Fact]
    public async Task 保存后恢复保持构图且两个Document实例互相隔离()
    {
        using var firstFixture = new DocumentFixture();
        using var secondFixture = new DocumentFixture();
        var first = firstFixture.CreateDocument();
        var second = secondFixture.CreateDocument();
        await first.InitializeAsync(new NewDocumentActivation("作品 A"), CancellationToken.None);
        first.ConstantReal = "-0.91";
        first.ConstantImaginary = "0.22";
        first.Seed = 2027;
        var snapshot = await first.CaptureSaveSnapshotAsync(CancellationToken.None);

        await second.InitializeAsync(
            new RestoreDocumentActivation("作品 A 恢复", snapshot.Content),
            CancellationToken.None);

        Assert.Equal(first.Artwork, second.Artwork);
        Assert.False(second.IsDirty);
        second.Scale = "2.25";
        Assert.NotEqual(first.Scale, second.Scale);
        Assert.Equal("作品 A 恢复", second.Presentation.Title);
    }

    [Fact]
    public async Task 撤销重做只操作当前Document的不可变作品快照()
    {
        using var fixture = new DocumentFixture();
        var document = fixture.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("历史"), CancellationToken.None);
        var original = document.Scale;
        document.Scale = "2.4";

        document.UndoCommand.Execute(null);
        Assert.Equal(original, document.Scale);
        Assert.True(document.CanRedo);

        document.RedoCommand.Execute(null);
        Assert.Equal(2.4, ArbitraryDecimal.Parse(document.Scale).ToDouble(), 12);
        Assert.True(document.CanUndo);
    }

    [Fact]
    public async Task 损坏恢复在发布状态前失败且不会执行渲染()
    {
        using var fixture = new DocumentFixture();
        var document = fixture.CreateDocument();
        var content = new DocumentContent(1, System.Text.Json.JsonSerializer.SerializeToElement(new { formatVersion = 1 }));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await document.InitializeAsync(new RestoreDocumentActivation("坏作品", content), CancellationToken.None));

        Assert.Equal(0, fixture.Pipeline.CallCount);
        Assert.False(document.IsDirty);
    }

    [Fact]
    public async Task 快速连续改参时迟到旧画面不能覆盖最新画面()
    {
        var pipeline = new ControlledPipeline();
        using var fixture = new DocumentFixture(pipeline);
        var document = fixture.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("并发"), CancellationToken.None);

        document.ConstantReal = "-0.5";
        var oldTask = document.RenderPreviewNowAsync();
        await Task.Delay(10);
        document.ConstantReal = "-0.6";
        var latestTask = document.RenderPreviewNowAsync();
        await Task.WhenAll(oldTask, latestTask);

        var expected = RenderFingerprint.Create(ControlledPipeline.CreateImage("-0.6"));
        Assert.Equal(expected, document.LastPreviewFingerprint);
    }

    [Fact]
    public async Task 初始化和显式渲染都观察取消令牌()
    {
        using var fixture = new DocumentFixture();
        var document = fixture.CreateDocument();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await document.InitializeAsync(new NewDocumentActivation("取消"), cancellation.Token));

        Assert.Equal(0, fixture.Pipeline.CallCount);
    }

    [Fact]
    public async Task 连续拖动合并为一次撤销且滚轮改变高精度视口()
    {
        using var fixture = new DocumentFixture();
        var document = fixture.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("交互"), CancellationToken.None);
        var originalCenter = ArbitraryDecimal.Parse(document.CenterX);
        var originalScale = ArbitraryDecimal.Parse(document.Scale);

        document.BeginViewportInteraction();
        document.PanViewport(10, 0, 600);
        document.PanViewport(10, 0, 600);
        document.EndViewportInteraction();

        Assert.True(document.CanUndo);
        Assert.NotEqual(originalCenter, ArbitraryDecimal.Parse(document.CenterX));
        document.UndoCommand.Execute(null);
        Assert.Equal(originalCenter, ArbitraryDecimal.Parse(document.CenterX));
        Assert.False(document.CanUndo);

        document.ZoomViewport(400, 300, 800, 600, 1);
        Assert.True(ArbitraryDecimal.Parse(document.Scale).CompareTo(originalScale) < 0);
    }

    [Fact]
    public async Task 交互先更新暂态呈现而真实帧提交后复位()
    {
        var pipeline = new ProgressivePipeline();
        using var fixture = new DocumentFixture(pipeline);
        var document = fixture.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("渐进预览"), CancellationToken.None);

        document.BeginViewportInteraction();
        document.PanViewport(12, -7, 600);

        Assert.False(document.TransientPreview.IsIdentity);
        Assert.Equal(12d, document.TransientPreview.OffsetX);
        Assert.Equal(-7d, document.TransientPreview.OffsetY);
        await pipeline.WaitForCallsAsync(2).WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => document.TransientPreview.IsIdentity);
        document.EndViewportInteraction();
    }

    [Fact]
    public async Task 精细交互预览先提交低成本真实帧再提升质量()
    {
        var pipeline = new ProgressivePipeline();
        using var fixture = new DocumentFixture(pipeline);
        var document = fixture.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("两阶段"), CancellationToken.None);

        document.HighQualityPreview = true;
        await pipeline.WaitForCallsAsync(3).WaitAsync(TimeSpan.FromSeconds(5));

        var contexts = pipeline.Contexts.ToArray();
        Assert.Equal(480, contexts[0].Width); // 初始化时的普通草稿。
        Assert.Equal(480, contexts[1].Width); // 交互稳定后先给低成本真实帧。
        Assert.Equal(960, contexts[2].Width); // 用户请求精细质量时随后提升。
    }

    [Fact]
    public async Task 配置精度不足会在状态栏明确报告()
    {
        using var fixture = new DocumentFixture();
        var document = fixture.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("精度不足"), CancellationToken.None);

        document.PrecisionDigits = 32;
        document.Scale = "1e-24";

        Assert.Contains("配置精度不足", document.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 九宫格采用收藏恢复与续变都通过Document历史和配方工作()
    {
        using var fixture = new DocumentFixture();
        var document = fixture.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("变体闭环"), CancellationToken.None);
        var original = document.Artwork.ToVariationRecipe();

        await document.GenerateVariationsCommand.ExecuteAsync(null);

        Assert.Equal(9, document.VariationCandidates.Count);
        Assert.Equal(1, document.Artwork.Exploration.Generation);
        var selected = document.VariationCandidates[0];
        document.ToggleFavoriteCommand.Execute(selected);
        Assert.Single(document.Favorites);
        document.ApplyVariationCommand.Execute(selected);
        Assert.Equal(selected.Definition.Recipe, document.Artwork.ToVariationRecipe());
        Assert.NotEqual(original, document.Artwork.ToVariationRecipe());

        document.UndoCommand.Execute(null);
        Assert.Equal(original, document.Artwork.ToVariationRecipe());
        document.RestoreFavoriteCommand.Execute(document.Favorites[0]);
        Assert.Equal(selected.Definition.Recipe, document.Artwork.ToVariationRecipe());

        var snapshot = await document.CaptureSaveSnapshotAsync(CancellationToken.None);
        var restored = new ArtworkSnapshotCodec(new ArtworkValidator()).Decode(snapshot.Content);
        Assert.Single(restored.Exploration.Favorites);
        Assert.Equal(selected.Definition.Recipe, restored.Exploration.Favorites[0].Recipe);
    }

    [Fact]
    public async Task 取消九宫格不会污染当前作品或上一批候选()
    {
        var pipeline = new BlockingVariationPipeline();
        using var fixture = new DocumentFixture(pipeline);
        var document = fixture.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("取消变体"), CancellationToken.None);
        var before = document.Artwork;

        var explore = document.GenerateVariationsCommand.ExecuteAsync(null);
        await pipeline.VariationStarted.WaitAsync(TimeSpan.FromSeconds(5));
        document.CancelOperationCommand.Execute(null);
        await explore;

        Assert.Equal(before, document.Artwork);
        Assert.Empty(document.VariationCandidates);
        Assert.Contains("保持不变", document.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 递归树与Julia共用Document历史保存变体和导出编排状态()
    {
        using var firstFixture = new DocumentFixture();
        using var restoredFixture = new DocumentFixture();
        var document = firstFixture.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("路径作品"), CancellationToken.None);

        document.ApplyArtworkPresetCommand.Execute("verdant-growth");
        document.TreeDepth = 8;
        document.TreeBranches = 3;
        document.TreeBranchAngle = 30;
        document.TreeLengthDecay = 0.64;
        document.TreeRandomness = 0.25;
        document.TreeStrokeWidth = 6;
        var treeBeforePan = document.Artwork.RecursiveTree;
        document.PanViewport(50, 40, 600);
        await document.GenerateVariationsCommand.ExecuteAsync(null);

        Assert.True(document.IsRecursiveTreeGenerator);
        Assert.False(document.IsJuliaGenerator);
        Assert.Equal(treeBeforePan, document.Artwork.RecursiveTree);
        Assert.Equal(9, document.VariationCandidates.Count);
        Assert.All(document.Artwork.Exploration.Candidates, candidate =>
            Assert.Equal(FractalGeneratorKind.RecursiveTree, candidate.Recipe.GeneratorKind));

        var snapshot = await document.CaptureSaveSnapshotAsync(CancellationToken.None);
        var restored = restoredFixture.CreateDocument();
        await restored.InitializeAsync(
            new RestoreDocumentActivation("路径作品恢复", snapshot.Content),
            CancellationToken.None);

        Assert.Equal(document.Artwork.GeneratorKind, restored.Artwork.GeneratorKind);
        Assert.Equal(document.Artwork.RecursiveTree, restored.Artwork.RecursiveTree);
        Assert.Equal(document.Artwork.Julia, restored.Artwork.Julia);
        Assert.Equal(document.Artwork.Gradient, restored.Artwork.Gradient);
        Assert.Equal(document.Artwork.Exploration.Generation, restored.Artwork.Exploration.Generation);
        Assert.Equal(document.Artwork.Exploration.Candidates, restored.Artwork.Exploration.Candidates);
        Assert.True(restored.IsRecursiveTreeGenerator);
        Assert.False(restored.IsDirty);
        restored.UndoCommand.Execute(null);
        Assert.False(restored.CanRedo); // 恢复不会伪造当前进程的编辑历史。
    }

    [Fact]
    public async Task 生成器切换后只编辑当前时间逃逸定义且LSystem规则可自定义()
    {
        using var fixture = new DocumentFixture();
        using var document = fixture.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("G0005.1"), CancellationToken.None);

        document.SelectGeneratorCommand.Execute("Mandelbrot");
        document.CenterX = "-0.75";

        Assert.True(document.IsEscapeTimeFamily);
        Assert.True(document.IsMandelbrotGenerator);
        Assert.Equal("-7.5e-1", document.Artwork.Mandelbrot.CenterX);
        Assert.Equal("0", document.Artwork.Julia.CenterX);

        document.SelectGeneratorCommand.Execute("LSystem");
        document.LSystemAxiom = "F";
        document.LSystemRulesText = "F=F+F";
        document.LSystemIterations = 3;

        Assert.True(document.IsLSystemFamily);
        Assert.True(document.IsLSystemGenerator);
        Assert.Equal([new LSystemRuleDefinition('F', "F+F")], document.Artwork.LSystem.Rules);
        Assert.Contains("8 条线段", document.LSystemDiagnostics, StringComparison.Ordinal);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class DocumentFixture : IDisposable
    {
        private readonly TestLifetime _lifetime = new();
        private readonly IArtworkRenderPipeline _pipeline;

        public DocumentFixture(IArtworkRenderPipeline? pipeline = null)
        {
            _pipeline = pipeline ?? new ImmediatePipeline();
        }

        public ImmediatePipeline Pipeline => Assert.IsType<ImmediatePipeline>(_pipeline);

        public FractalArtworkDocument CreateDocument()
        {
            var validator = new ArtworkValidator();
            var generator = new VariationGenerator(validator);
            return new FractalArtworkDocument(
                validator,
                new ArtworkSnapshotCodec(validator),
                _pipeline,
                new NullPreviewFactory(),
                new NullExporter(),
                new NullExportDialog(),
                new ArtworkHistory(),
                new ArtisticParameterMapper(),
                new VariationExplorer(generator, _pipeline),
                new ArtworkPresetCatalog(),
                _lifetime);
        }

        public void Dispose() => _lifetime.Dispose();
    }

    private sealed class ImmediatePipeline : IArtworkRenderPipeline
    {
        public int CallCount { get; private set; }

        public Task<RgbaImage> RenderAsync(ArtworkDefinition artwork, RenderContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(ControlledPipeline.CreateImage(artwork.Julia.ConstantReal));
        }
    }

    private sealed class ControlledPipeline : IArtworkRenderPipeline
    {
        private int _callCount;

        public async Task<RgbaImage> RenderAsync(ArtworkDefinition artwork, RenderContext context, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _callCount);
            if (call > 1)
            {
                // 特意忽略取消，模拟底层库迟到返回；Document 仍必须在提交前拦截。
                await Task.Delay(ArbitraryDecimal.Parse(artwork.Julia.ConstantReal).ToDouble() == -0.5 ? 120 : 10);
            }

            return CreateImage(artwork.Julia.ConstantReal);
        }

        public static RgbaImage CreateImage(string constantRealText)
        {
            var constantReal = ArbitraryDecimal.Parse(constantRealText).ToDouble();
            var value = (byte)Math.Clamp((int)Math.Round((constantReal + 2) * 50), 0, 255);
            return new RgbaImage(1, 1, [value, 0, 0, 255]);
        }
    }

    private sealed class ProgressivePipeline : IArtworkRenderPipeline
    {
        private readonly object _sync = new();
        private readonly List<RenderContext> _contexts = [];
        private readonly List<(int Target, TaskCompletionSource Completion)> _waiters = [];

        public IReadOnlyList<RenderContext> Contexts
        {
            get
            {
                lock (_sync)
                {
                    return _contexts.ToArray();
                }
            }
        }

        public Task WaitForCallsAsync(int count)
        {
            lock (_sync)
            {
                if (_contexts.Count >= count)
                {
                    return Task.CompletedTask;
                }

                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((count, completion));
                return completion.Task;
            }
        }

        public Task<RgbaImage> RenderAsync(
            ArtworkDefinition artwork,
            RenderContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                _contexts.Add(context);
                foreach (var waiter in _waiters.Where(waiter => _contexts.Count >= waiter.Target).ToArray())
                {
                    waiter.Completion.TrySetResult();
                    _waiters.Remove(waiter);
                }
            }

            return Task.FromResult(ControlledPipeline.CreateImage(artwork.Julia.ConstantReal));
        }
    }

    private sealed class BlockingVariationPipeline : IArtworkRenderPipeline
    {
        private int _calls;
        private readonly TaskCompletionSource _variationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task VariationStarted => _variationStarted.Task;

        public async Task<RgbaImage> RenderAsync(
            ArtworkDefinition artwork,
            RenderContext context,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                return ControlledPipeline.CreateImage(artwork.Julia.ConstantReal);
            }

            _variationStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("取消后不应到达此处。");
        }
    }

    private sealed class NullPreviewFactory : IPreviewImageFactory
    {
        public Bitmap? Create(RgbaImage image, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    private sealed class NullExporter : IArtworkExporter
    {
        public Task ExportAsync(ArtworkDefinition artwork, string path, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class NullExportDialog : IArtworkExportDialog
    {
        public Task<string?> PickPngPathAsync(string suggestedName, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);
    }

    private sealed class TestLifetime : IDocumentLifetime, IDisposable
    {
        private readonly CancellationTokenSource _source = new();
        public CancellationToken ClosingToken => _source.Token;
        public bool IsClosing => _source.IsCancellationRequested;
        public void Dispose()
        {
            if (!_source.IsCancellationRequested)
            {
                _source.Cancel();
            }

            _source.Dispose();
        }
    }
}
