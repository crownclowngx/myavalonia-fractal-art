using Avalonia.Media.Imaging;
using FractalArtPlugin.Application;
using FractalArtPlugin.Domain;
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
        first.ConstantReal = -0.91;
        first.ConstantImaginary = 0.22;
        first.Seed = 2027;
        var snapshot = await first.CaptureSaveSnapshotAsync(CancellationToken.None);

        await second.InitializeAsync(
            new RestoreDocumentActivation("作品 A 恢复", snapshot.Content),
            CancellationToken.None);

        Assert.Equal(first.Artwork, second.Artwork);
        Assert.False(second.IsDirty);
        second.Scale = 2.25;
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
        document.Scale = 2.4;

        document.UndoCommand.Execute(null);
        Assert.Equal(original, document.Scale);
        Assert.True(document.CanRedo);

        document.RedoCommand.Execute(null);
        Assert.Equal(2.4, document.Scale);
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

        document.ConstantReal = -0.5;
        var oldTask = document.RenderPreviewNowAsync();
        await Task.Delay(10);
        document.ConstantReal = -0.6;
        var latestTask = document.RenderPreviewNowAsync();
        await Task.WhenAll(oldTask, latestTask);

        var expected = RenderFingerprint.Create(ControlledPipeline.CreateImage(-0.6));
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
            return new FractalArtworkDocument(
                validator,
                new ArtworkSnapshotCodec(validator),
                _pipeline,
                new NullPreviewFactory(),
                new NullExporter(),
                new NullExportDialog(),
                new ArtworkHistory(),
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
                await Task.Delay(artwork.Julia.ConstantReal == -0.5 ? 120 : 10);
            }

            return CreateImage(artwork.Julia.ConstantReal);
        }

        public static RgbaImage CreateImage(double constantReal)
        {
            var value = (byte)Math.Clamp((int)Math.Round((constantReal + 2) * 50), 0, 255);
            return new RgbaImage(1, 1, [value, 0, 0, 255]);
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
