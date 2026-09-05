using System.Buffers.Binary;
using System.IO.Compression;
using FractalArtPlugin.Application;
using FractalArtPlugin.Infrastructure;
using Xunit;

namespace FractalArtPlugin.Tests;

public sealed class G0011StaticClosureTests
{
    [Fact]
    public void 导出计划使用会话尺寸透明背景与最终质量且不修改原作品()
    {
        var validator = new ArtworkValidator();
        var planner = new ArtworkExportPlanner(validator, validator);
        var artwork = ArtworkDefinition.CreateDefault();

        var plan = planner.Create(artwork, new ArtworkExportRequest(3840, 2160, true));

        Assert.Equal(3840, plan.Artwork.Canvas.Width);
        Assert.Equal(2160, plan.Artwork.Canvas.Height);
        Assert.Equal(0, plan.Artwork.Canvas.Background.Alpha);
        Assert.Equal(artwork.Canvas.Background.Red, plan.Artwork.Canvas.Background.Red);
        Assert.Equal(artwork.Gradient, plan.Artwork.Gradient);
        Assert.Equal(RenderQuality.Final, plan.Context.Quality);
        Assert.Equal((3840, 2160), (plan.Context.Width, plan.Context.Height));
        Assert.Equal((1200, 800, byte.MaxValue),
            (artwork.Canvas.Width, artwork.Canvas.Height, artwork.Canvas.Background.Alpha));
    }

    [Fact]
    public async Task 透明计划经真实路径与合成管线保留图形Alpha并清空画布空白()
    {
        var validator = new ArtworkValidator();
        var artwork = ArtworkDefinition.CreateDefault()
            .WithGeneratorKind(FractalGeneratorKind.RecursiveTree) with
        {
            Canvas = new CanvasDefinition(64, 64, new RgbaColor(12, 34, 56, 255))
        };
        var plan = new ArtworkExportPlanner(validator, validator).Create(
            artwork,
            new ArtworkExportRequest(64, 64, true));

        var result = await TestArtworkPipeline.Create().RenderAsync(
            plan.Artwork,
            plan.Context,
            CancellationToken.None);
        var renderedPixels = result.Image.Pixels.ToArray();
        var alpha = Enumerable.Range(0, renderedPixels.Length / 4)
            .Select(index => renderedPixels[index * 4 + 3]).ToArray();

        Assert.Contains((byte)0, alpha);
        Assert.Contains(alpha, value => value > 0);
        var encoded = new PngEncoder().Encode(result.Image, CancellationToken.None);
        var pixels = DecodeRgba(encoded, 64, 64);
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            if (pixels[offset + 3] == 0)
            {
                Assert.Equal(new byte[] { 0, 0, 0 }, pixels.AsSpan(offset, 3).ToArray());
            }
        }
    }

    [Fact]
    public void 导出计划复用作品预算并在文件选择前拒绝非法或缺失能力()
    {
        var validator = new ArtworkValidator();
        var planner = new ArtworkExportPlanner(validator, validator);
        var artwork = ArtworkDefinition.CreateDefault();

        Assert.Throws<InvalidDataException>(() =>
            planner.Create(artwork, new ArtworkExportRequest(8193, 800, false)));

        var unavailable = new UnavailableEffectDefinition(
            "future.effect", 2, true, "{\"strength\":3}");
        var blocked = artwork with
        {
            MasterEffects = new EffectChainDefinition(1, artwork.MasterEffects.Effects.Append(unavailable))
        };
        Assert.Throws<NotSupportedException>(() =>
            planner.Create(blocked, new ArtworkExportRequest(1200, 800, false)));
    }

    [Fact]
    public void Png固定RGBA色彩元数据并清除全透明像素隐藏颜色()
    {
        var encoded = new PngEncoder().Encode(
            new ImageSurface(2, 1, [10, 20, 30, 255, 90, 80, 70, 0]),
            CancellationToken.None);
        var chunks = ReadChunks(encoded);

        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, encoded[..8]);
        Assert.Equal(8, chunks["IHDR"][8]);
        Assert.Equal(6, chunks["IHDR"][9]);
        Assert.Equal(0, chunks["IHDR"][12]);
        Assert.Equal(0, Assert.Single(chunks["sRGB"]));
        Assert.Equal(45455u, BinaryPrimitives.ReadUInt32BigEndian(chunks["gAMA"]));

        using var compressed = new MemoryStream(chunks["IDAT"]);
        using var inflater = new ZLibStream(compressed, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflater.CopyTo(raw);
        Assert.Equal(new byte[] { 0, 10, 20, 30, 255, 0, 0, 0, 0 }, raw.ToArray());
    }

    [Fact]
    public void 缩略图上下文保持宽高比质量与单图并行预算()
    {
        var artwork = ArtworkDefinition.CreateDefault() with
        {
            Canvas = new CanvasDefinition(1600, 900, ArtworkDefinition.CreateDefault().Canvas.Background),
            Presentation = ArtworkDefinition.CreateDefault().Presentation with { HighQualityPreview = true }
        };

        var thumbnail = RenderContext.ForThumbnail(artwork, 240);

        Assert.Equal((240, 135), (thumbnail.Width, thumbnail.Height));
        Assert.Equal(RenderQuality.Draft, thumbnail.Quality);
        Assert.Equal(1, thumbnail.MaxDegreeOfParallelism);
        Assert.Equal(artwork.Seed, thumbnail.Seed);
    }

    [Fact]
    public void 缺失能力报告可逐项显式移除且其它不透明配置保持原样()
    {
        var validator = new ArtworkValidator();
        var service = new ArtworkCompatibilityService(validator);
        var artwork = ArtworkDefinition.CreateDefault();
        var unknownLayer = new UnavailableLayerDefinition(
            "future-layer", "未来图层", true, 1, LayerBlendMode.Normal,
            LayerTransformDefinition.Identity, null, "future.layer", 3, "{\"tokens\":[1,2]}");
        var unknownEffect = new UnavailableEffectDefinition(
            "future.effect", 4, true, "{\"radius\":9}");
        artwork = artwork with
        {
            Layers = artwork.Layers.Append(unknownLayer).ToArray(),
            MasterEffects = new EffectChainDefinition(1, artwork.MasterEffects.Effects.Append(unknownEffect))
        };

        var report = service.Inspect(artwork);
        Assert.False(report.CanRender);
        Assert.Equal(2, report.Issues.Count);

        var withoutLayer = service.Remove(artwork, report.Issues.Single(issue =>
            issue.Kind == ArtworkCompatibilityIssueKind.Layer).Key);
        Assert.DoesNotContain(withoutLayer.Layers, layer => layer.Id == unknownLayer.Id);
        var remainingEffect = Assert.IsType<UnavailableEffectDefinition>(withoutLayer.MasterEffects.Effects[^1]);
        Assert.Equal(unknownEffect.OpaquePayload, remainingEffect.OpaquePayload);

        var repaired = service.Remove(withoutLayer, service.Inspect(withoutLayer).Issues.Single().Key);
        Assert.True(service.Inspect(repaired).CanRender);
        validator.EnsureRenderable(repaired);
    }

    private static Dictionary<string, byte[]> ReadChunks(byte[] png)
    {
        var chunks = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var offset = 8;
        while (offset < png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            var data = png.AsSpan(offset + 8, length).ToArray();
            if (type == "IDAT" && chunks.TryGetValue(type, out var existing))
            {
                chunks[type] = existing.Concat(data).ToArray();
            }
            else
            {
                chunks[type] = data;
            }

            offset += 12 + length;
        }

        return chunks;
    }

    private static byte[] DecodeRgba(byte[] png, int width, int height)
    {
        var chunks = ReadChunks(png);
        using var compressed = new MemoryStream(chunks["IDAT"]);
        using var inflater = new ZLibStream(compressed, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflater.CopyTo(raw);
        var scanlines = raw.ToArray();
        var rowBytes = width * 4;
        var pixels = new byte[rowBytes * height];
        for (var y = 0; y < height; y++)
        {
            Assert.Equal(0, scanlines[y * (rowBytes + 1)]);
            scanlines.AsSpan(y * (rowBytes + 1) + 1, rowBytes).CopyTo(pixels.AsSpan(y * rowBytes));
        }

        return pixels;
    }
}
