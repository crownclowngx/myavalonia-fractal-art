using FractalArtPlugin.Domain;
using Xunit;

namespace FractalArtPlugin.Tests;

public sealed class ArbitraryPrecisionTests
{
    [Fact]
    public void 十进制模型保留Double完全无法表达的细微差值()
    {
        var one = ArbitraryDecimal.Parse("1");
        var microscopic = ArbitraryDecimal.Parse("1e-1000");

        var result = one.Add(microscopic, 1024);

        Assert.NotEqual(one, result);
        Assert.Equal(0, result.Subtract(one, 1024).CompareTo(microscopic));
        Assert.Equal(1d, result.ToDouble()); // double 会丢失该差值，领域模型不会。
    }

    [Fact]
    public void 深度尺度自动选择任意精度并使用保守预览预算()
    {
        var artwork = ArtworkDefinition.CreateDefault() with
        {
            Canvas = ArtworkDefinition.CreateDefault().Canvas with { Width = 1600, Height = 900 },
            Julia = ArtworkDefinition.CreateDefault().Julia with { Scale = "1e-40", PrecisionDigits = 96 }
        };

        var context = RenderContext.ForPreview(artwork);

        Assert.Equal(NumericPrecision.Arbitrary, context.NumericPrecision);
        Assert.Equal(320, context.Width);
        Assert.Equal(180, context.Height);
        Assert.Equal(96, context.PrecisionDigits);
    }

    [Fact]
    public void 千位精度使用更严格的交互像素预算()
    {
        var artwork = ArtworkDefinition.CreateDefault() with
        {
            Canvas = ArtworkDefinition.CreateDefault().Canvas with { Width = 1600, Height = 900 },
            Julia = ArtworkDefinition.CreateDefault().Julia with
            {
                Scale = "1e-1000",
                PrecisionDigits = 1024,
                ForceHighPrecision = true
            }
        };

        var draft = RenderContext.ForPreview(artwork);
        var detailed = RenderContext.ForPreview(artwork with
        {
            Presentation = artwork.Presentation with { HighQualityPreview = true }
        });

        Assert.Equal(96, draft.Width);
        Assert.Equal(54, draft.Height);
        Assert.Equal(144, detailed.Width);
        Assert.Equal(81, detailed.Height);
    }

    [Fact]
    public void 极小尺度平移后中心差值不会坍缩为零()
    {
        var viewport = ArtworkDefinition.CreateDefault().Julia with
        {
            Scale = "1e-350",
            PrecisionDigits = 400,
            ForceHighPrecision = true
        };

        var moved = HighPrecisionViewport.Pan(viewport, 1, 0, 1000);
        var center = ArbitraryDecimal.Parse(moved.CenterX);

        Assert.NotEqual(ArbitraryDecimal.Zero, center);
        Assert.Equal(-353, center.AdjustedExponent);
        Assert.Equal(0d, center.ToDouble());
    }

    [Fact]
    public void 滚轮缩放保持鼠标锚点且中心点缩放不漂移()
    {
        var viewport = ArtworkDefinition.CreateDefault().Julia with { PrecisionDigits = 128 };

        var centered = HighPrecisionViewport.ZoomAt(viewport, 400, 300, 800, 600, 1);
        var offset = HighPrecisionViewport.ZoomAt(viewport, 600, 300, 800, 600, 1);
        var oldAnchor = AnchorX(viewport, 600, 800, 600);
        var newAnchor = AnchorX(offset, 600, 800, 600);

        Assert.Equal(ArbitraryDecimal.Parse(viewport.CenterX), ArbitraryDecimal.Parse(centered.CenterX));
        Assert.Equal(2.56, ArbitraryDecimal.Parse(centered.Scale).ToDouble(), 12);
        Assert.Equal(0, oldAnchor.CompareTo(newAnchor));
    }

    [Fact]
    public async Task 任意精度Julia内核在深度尺度下保持确定性和可取消性()
    {
        var definition = ArtworkDefinition.CreateDefault().Julia with
        {
            CenterX = "-7.45e-1",
            CenterY = "1.13e-1",
            Scale = "1e-40",
            MaxIterations = 32,
            ForceHighPrecision = true,
            PrecisionDigits = 96
        };
        var context = new RenderContext(
            12, 8, RenderQuality.Draft, 42, RenderContext.CurrentRendererVersion, NumericPrecision.Arbitrary, 96);
        var generator = new JuliaFieldGenerator();

        var first = await generator.GenerateAsync(definition, context, CancellationToken.None);
        var second = await generator.GenerateAsync(definition, context, CancellationToken.None);

        Assert.Equal(first.Values, second.Values);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            generator.GenerateAsync(definition, context, cancellation.Token));
    }

    private static ArbitraryDecimal AnchorX(JuliaDefinition viewport, int pointerX, int viewportWidth, int viewportHeight)
    {
        var offsetPixels = ArbitraryDecimal.Parse((pointerX - viewportWidth / 2).ToString());
        var offset = ArbitraryDecimal.Parse(viewport.Scale)
            .Multiply(offsetPixels, viewport.PrecisionDigits)
            .Divide(viewportHeight, viewport.PrecisionDigits);
        return ArbitraryDecimal.Parse(viewport.CenterX).Add(offset, viewport.PrecisionDigits);
    }
}
