using Xunit;

namespace FractalArtPlugin.Tests.Domain.Rendering;

public sealed class G0008CompositionTests
{
    [Theory]
    [InlineData(LayerBlendMode.Normal, 128, 64, 32)]
    [InlineData(LayerBlendMode.Multiply, 32, 32, 24)]
    [InlineData(LayerBlendMode.Screen, 160, 160, 200)]
    [InlineData(LayerBlendMode.Add, 192, 192, 224)]
    [InlineData(LayerBlendMode.Overlay, 64, 65, 145)]
    public void 五种混合模式具有稳定枚举值与不透明像素结果(
        LayerBlendMode mode,
        byte red,
        byte green,
        byte blue)
    {
        var compositor = new LayerCompositor();
        var backdrop = Pixel(64, 128, 192, 255);
        var source = Pixel(128, 64, 32, 255);

        var actual = compositor.Composite(backdrop, source, 1, mode, null, CancellationToken.None);

        Assert.Equal([(byte)red, (byte)green, (byte)blue, (byte)255], actual.Pixels.ToArray());
        Assert.Equal((int)mode, mode switch
        {
            LayerBlendMode.Normal => 0,
            LayerBlendMode.Multiply => 1,
            LayerBlendMode.Screen => 2,
            LayerBlendMode.Add => 3,
            LayerBlendMode.Overlay => 4,
            _ => -1
        });
    }

    [Fact]
    public void Alpha透明边界遮罩与不透明度遵循SourceOver()
    {
        var compositor = new LayerCompositor();
        var backdrop = Pixel(10, 20, 30, 255);

        var transparent = compositor.Composite(
            backdrop, Pixel(200, 100, 50, 0), 1, LayerBlendMode.Normal, null, CancellationToken.None);
        var masked = compositor.Composite(
            backdrop, Pixel(200, 100, 50, 255), 1, LayerBlendMode.Normal,
            new Mask(1, 1, [0]), CancellationToken.None);
        var half = compositor.Composite(
            Pixel(0, 0, 0, 0), Pixel(200, 100, 50, 255), 0.5,
            LayerBlendMode.Normal, null, CancellationToken.None);

        Assert.Equal(backdrop.Pixels.ToArray(), transparent.Pixels.ToArray());
        Assert.Equal(backdrop.Pixels.ToArray(), masked.Pixels.ToArray());
        Assert.Equal([200, 100, 50, 128], half.Pixels.ToArray());
    }

    [Fact]
    public void 变换使用逆向采样且越界透明并保持预乘Alpha边缘()
    {
        var pixels = new byte[4 * 4 * 4];
        WritePixel(pixels, 4, 1, 1, 255, 0, 0, 128);
        var source = new ImageSurface(4, 4, pixels);
        var transformer = new LayerRasterTransformer();

        var translated = transformer.Transform(source,
            LayerTransformDefinition.Identity with { PositionXPercent = 25 }, CancellationToken.None);
        var rotated = transformer.Transform(source,
            LayerTransformDefinition.Identity with { RotationDegrees = 90 }, CancellationToken.None);

        Assert.Equal([255, 0, 0, 128], ReadPixel(translated, 2, 1));
        Assert.Equal([0, 0, 0, 0], ReadPixel(translated, 1, 1));
        Assert.Equal([255, 0, 0, 128], ReadPixel(rotated, 2, 1));
        Assert.All(Enumerable.Range(0, 16)
            .Where(index => index != 6)
            .Select(index => rotated.Pixels.Span[index * 4 + 3]), value => Assert.Equal(0, value));
    }

    [Fact]
    public void 位置缩放旋转与非中心锚点组合具有固定插值指纹()
    {
        var pixels = new byte[5 * 5 * 4];
        WritePixel(pixels, 5, 1, 1, 255, 20, 10, 96);
        WritePixel(pixels, 5, 2, 2, 20, 240, 80, 180);
        WritePixel(pixels, 5, 3, 1, 40, 80, 255, 255);
        var transform = new LayerTransformDefinition(20, -20, 125, 30, 20, 80);

        var result = new LayerRasterTransformer().Transform(
            new ImageSurface(5, 5, pixels), transform, CancellationToken.None);

        Assert.Equal("38c7e43c4217f54c", RenderFingerprint.Create(result));
    }

    [Fact]
    public void Mask阈值柔化反相和内部点规则确定()
    {
        var converter = new ScalarMaskConverter();
        var field = new ScalarField(3, 1, [1f, 0.5f, 1f], [false, true, true]);
        var normal = converter.Convert(field, new ScalarMaskDefinition("source", 0.5, 1, false), CancellationToken.None);
        var inverted = converter.Convert(field, new ScalarMaskDefinition("source", 0.5, 1, true), CancellationToken.None);

        Assert.Equal([0, 128, 255], normal.Values.ToArray());
        Assert.Equal([255, 128, 0], inverted.Values.ToArray());
    }

    [Fact]
    public void 色调后Bloom顺序具有固定RGBA指纹且透明像素保持无隐藏颜色()
    {
        var pixels = new byte[4 * 4 * 4];
        WritePixel(pixels, 4, 1, 1, 240, 120, 30, 255);
        WritePixel(pixels, 4, 2, 2, 10, 80, 220, 192);
        pixels[0] = 250; // 非法的透明隐藏颜色应被色调清零，而不是扩散到 Bloom。
        var effects = new EffectChainDefinition(1,
        [
            new ToneEffectDefinition(true, 0.08, 0.2, 1.3),
            new BloomEffectDefinition(true, 0.25, 1.1, 0.7)
        ]);

        var result = new MasterEffectRenderer().Apply(new ImageSurface(4, 4, pixels), effects, CancellationToken.None);

        Assert.Equal([0, 0, 0, 0], ReadPixel(result, 0, 0));
        Assert.Equal("cabbdd3415b43a3f", RenderFingerprint.Create(result));
    }

    private static ImageSurface Pixel(byte red, byte green, byte blue, byte alpha) =>
        new(1, 1, [red, green, blue, alpha]);

    private static void WritePixel(
        byte[] pixels,
        int width,
        int x,
        int y,
        byte red,
        byte green,
        byte blue,
        byte alpha)
    {
        var offset = (y * width + x) * 4;
        pixels[offset] = red;
        pixels[offset + 1] = green;
        pixels[offset + 2] = blue;
        pixels[offset + 3] = alpha;
    }

    private static byte[] ReadPixel(ImageSurface image, int x, int y) =>
        image.Pixels.Span.Slice((y * image.Width + x) * 4, 4).ToArray();
}
