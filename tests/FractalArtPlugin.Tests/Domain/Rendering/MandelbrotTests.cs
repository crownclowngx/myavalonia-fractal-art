using Xunit;

namespace FractalArtPlugin.Tests.Domain.Rendering;

public sealed class MandelbrotTests
{
    [Fact]
    public async Task Double内核使用像素作为C且中心零点不逃逸()
    {
        var definition = new MandelbrotDefinition("0", "0", "4", 64, false, 64);
        var context = CreateContext(NumericPrecision.Double);

        var field = await new MandelbrotFieldGenerator().GenerateAsync(definition, context, CancellationToken.None);

        Assert.False(field.Escaped[4]); // 中央像素 c=0，始终位于集合内部。
        Assert.True(field.Escaped[5]);  // 右侧中点 c=2，会在有限步内逃逸。
        Assert.Equal("mandelbrot-double", field.Diagnostics.Kernel);
    }

    [Fact]
    public async Task 任意精度参考内核在简单网格上保持相同内外分类()
    {
        var definition = new MandelbrotDefinition("0", "0", "4", 64, true, 80);
        var doubleField = await new MandelbrotFieldGenerator().GenerateAsync(
            definition,
            CreateContext(NumericPrecision.Double),
            CancellationToken.None);
        var arbitraryField = await new MandelbrotFieldGenerator().GenerateAsync(
            definition,
            CreateContext(NumericPrecision.Arbitrary),
            CancellationToken.None);

        Assert.Equal(doubleField.Escaped, arbitraryField.Escaped);
        Assert.Equal("mandelbrot-arbitrary-fixed", arbitraryField.Diagnostics.Kernel);
    }

    [Fact]
    public async Task Mandelbrot生成观察预先取消()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new MandelbrotFieldGenerator().GenerateAsync(
                ArtworkDefinition.CreateDefault().Mandelbrot,
                CreateContext(NumericPrecision.Double),
                cancellation.Token));
    }

    private static RenderContext CreateContext(NumericPrecision precision) => new(
        3,
        3,
        RenderQuality.Draft,
        1,
        RenderContext.CurrentRendererVersion,
        precision,
        80)
    {
        ConfiguredPrecisionDigits = 80,
        EffectivePrecisionDigits = 80,
        MaxDegreeOfParallelism = 1,
        ChunkHeight = 1,
        CancellationCheckInterval = 1
    };
}
