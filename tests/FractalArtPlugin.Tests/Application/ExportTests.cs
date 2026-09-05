using FractalArtPlugin.Application;
using FractalArtPlugin.Infrastructure;
using Xunit;

namespace FractalArtPlugin.Tests;

public sealed class ExportTests
{
    [Fact]
    public async Task 导出使用规范画布最终质量且输出真实Png而非界面截图()
    {
        var pipeline = new CapturingPipeline();
        var writer = new CapturingWriter();
        var exporter = new ArtworkExporter(pipeline, new PngEncoder(), writer);
        var artwork = ArtworkDefinition.CreateDefault() with
        {
            Canvas = ArtworkDefinition.CreateDefault().Canvas with { Width = 640, Height = 360 }
        };

        await exporter.ExportAsync(CreatePlan(artwork), "ignored.png", CancellationToken.None);

        Assert.NotNull(pipeline.Context);
        Assert.Equal(640, pipeline.Context.Width);
        Assert.Equal(360, pipeline.Context.Height);
        Assert.Equal(RenderQuality.Final, pipeline.Context.Quality);
        Assert.Equal(artwork.Seed, pipeline.Context.Seed);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, writer.Content[..8]);
    }

    [Fact]
    public async Task 预先取消的导出不会执行渲染或写入()
    {
        var pipeline = new CapturingPipeline();
        var writer = new CapturingWriter();
        var exporter = new ArtworkExporter(pipeline, new PngEncoder(), writer);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            exporter.ExportAsync(CreatePlan(ArtworkDefinition.CreateDefault()), "ignored.png", cancellation.Token));

        Assert.Null(pipeline.Context);
        Assert.Empty(writer.Content);
    }

    [Fact]
    public async Task 原子写入在取消时保留既有目标且清理临时文件()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"fractal-art-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "art.png");
        await File.WriteAllBytesAsync(target, [1, 2, 3]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new AtomicFileWriter().WriteAsync(target, new byte[1024], cancellation.Token));

            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(target));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void 草稿预览受尺寸预算限制而最终导出保持规范尺寸()
    {
        var artwork = ArtworkDefinition.CreateDefault() with
        {
            Canvas = new CanvasDefinition(4096, 2048, ArtworkDefinition.CreateDefault().Canvas.Background)
        };

        var preview = RenderContext.ForPreview(artwork);
        var final = RenderContext.ForExport(artwork);

        Assert.Equal(RenderQuality.Draft, preview.Quality);
        Assert.Equal(480, preview.Width);
        Assert.Equal(240, preview.Height);
        Assert.Equal(RenderQuality.Final, final.Quality);
        Assert.Equal(4096, final.Width);
        Assert.Equal(2048, final.Height);
    }

    private sealed class CapturingPipeline : IArtworkRenderPipeline
    {
        public RenderContext? Context { get; private set; }

        public Task<ArtworkRenderResult> RenderAsync(
            ArtworkDefinition artwork,
            RenderContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Context = context;
            return Task.FromResult(new ArtworkRenderResult(
                new ImageSurface(1, 1, [10, 20, 30, 255]),
                new ArtworkRenderExecutionSummary([], ["test"], 1)));
        }
    }

    private static ArtworkExportPlan CreatePlan(ArtworkDefinition artwork)
    {
        var validator = new ArtworkValidator();
        return new ArtworkExportPlanner(validator, validator).Create(
            artwork,
            new ArtworkExportRequest(artwork.Canvas.Width, artwork.Canvas.Height, false));
    }

    private sealed class CapturingWriter : IAtomicFileWriter
    {
        public byte[] Content { get; private set; } = [];

        public Task WriteAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Content = content.ToArray();
            return Task.CompletedTask;
        }
    }
}
