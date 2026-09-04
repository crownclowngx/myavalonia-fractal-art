using System.Buffers.Binary;
using FractalArtPlugin.Infrastructure;
using Xunit;

namespace FractalArtPlugin.Tests;

public sealed class RenderingTests
{
    [Fact]
    public async Task 相同配方与上下文生成完全一致的归一化标量场()
    {
        var generator = new JuliaFieldGenerator();
        var definition = ArtworkDefinition.CreateDefault().Julia with { MaxIterations = 96 };
        var context = new RenderContext(
            80, 60, RenderQuality.Final, 42, RenderContext.CurrentRendererVersion, NumericPrecision.Double, 96);

        var first = await generator.GenerateAsync(definition, context, CancellationToken.None);
        var second = await generator.GenerateAsync(definition, context, CancellationToken.None);

        Assert.Equal(first.Values, second.Values);
        Assert.Equal(first.Escaped, second.Escaped);
        Assert.All(first.Values, value => Assert.InRange(value, 0f, 1f));
        Assert.Contains(first.Escaped, value => value);
        Assert.Contains(first.Escaped, value => !value);
    }

    [Fact]
    public async Task 生成器和渐变映射都观察取消()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var generator = new JuliaFieldGenerator();
        var context = new RenderContext(
            64, 64, RenderQuality.Draft, 1, RenderContext.CurrentRendererVersion, NumericPrecision.Double, 96);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            generator.GenerateAsync(ArtworkDefinition.CreateDefault().Julia, context, cancellation.Token));

        var mapper = new LinearGradientMapper();
        var field = new ScalarField(1, 1, [0.5f], [true]);
        Assert.ThrowsAny<OperationCanceledException>(() =>
            mapper.Map(field, ArtworkDefinition.CreateDefault().Gradient, cancellation.Token));
    }

    [Fact]
    public void 渐变映射区分逃逸值与内部点()
    {
        var field = new ScalarField(3, 1, [0f, 1f, 1f], [true, true, false]);
        var gradient = new GradientDefinition(
            new RgbaColor(0, 10, 20),
            new RgbaColor(100, 110, 120),
            new RgbaColor(7, 8, 9));

        var image = new LinearGradientMapper().Map(field, gradient, CancellationToken.None);

        Assert.Equal(
            new byte[] { 0, 10, 20, 255, 100, 110, 120, 255, 7, 8, 9, 255 },
            image.Pixels);
    }

    [Fact]
    public void Png编码器产生带正确尺寸的标准签名和Ihdr()
    {
        var image = new ImageSurface(2, 1, [255, 0, 0, 255, 0, 255, 0, 128]);

        var bytes = new PngEncoder().Encode(image, CancellationToken.None);

        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes[..8]);
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(bytes, 12, 4));
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)));
        Assert.Equal("IEND", System.Text.Encoding.ASCII.GetString(bytes, bytes.Length - 8, 4));
    }
}
