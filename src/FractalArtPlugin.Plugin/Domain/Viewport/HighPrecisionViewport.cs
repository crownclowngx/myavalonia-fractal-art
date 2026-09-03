using System.Globalization;
using FractalArtPlugin.Domain.Artwork;
using FractalArtPlugin.Numerics;

namespace FractalArtPlugin.Domain.Viewport;

/// <summary>集中实现平移和锚点缩放；View 只表达输入意图，不直接操作高精度字符串。</summary>
public static class HighPrecisionViewport
{
    public static JuliaDefinition Pan(JuliaDefinition viewport, double deltaX, double deltaY, double viewportHeight)
    {
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY) || !double.IsFinite(viewportHeight) || viewportHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(viewportHeight), "视口尺寸和拖动距离必须是有限数值。");
        }

        var digits = viewport.PrecisionDigits;
        var scale = ArbitraryDecimal.Parse(viewport.Scale);
        var height = Math.Max(1, (int)Math.Round(viewportHeight));
        var xOffset = scale.Multiply(FromDouble(-deltaX), digits).Divide(height, digits);
        var yOffset = scale.Multiply(FromDouble(-deltaY), digits).Divide(height, digits);
        return viewport with
        {
            CenterX = ArbitraryDecimal.Parse(viewport.CenterX).Add(xOffset, digits).ToString(),
            CenterY = ArbitraryDecimal.Parse(viewport.CenterY).Add(yOffset, digits).ToString()
        };
    }

    public static JuliaDefinition ZoomAt(
        JuliaDefinition viewport,
        double pointerX,
        double pointerY,
        double viewportWidth,
        double viewportHeight,
        double wheelDelta)
    {
        if (!double.IsFinite(pointerX) || !double.IsFinite(pointerY) ||
            !double.IsFinite(viewportWidth) || !double.IsFinite(viewportHeight) ||
            viewportWidth <= 0 || viewportHeight <= 0 || wheelDelta == 0)
        {
            return viewport;
        }

        var digits = viewport.PrecisionDigits;
        var steps = Math.Clamp((int)Math.Ceiling(Math.Abs(wheelDelta)), 1, 8);
        var factor = ArbitraryDecimal.One;
        var stepFactor = ArbitraryDecimal.Parse(wheelDelta > 0 ? "0.8" : "1.25");
        for (var step = 0; step < steps; step++)
        {
            factor = factor.Multiply(stepFactor, digits);
        }

        var scale = ArbitraryDecimal.Parse(viewport.Scale);
        var height = Math.Max(1, (int)Math.Round(viewportHeight));
        var xOffset = scale.Multiply(FromDouble(pointerX - viewportWidth / 2d), digits).Divide(height, digits);
        var yOffset = scale.Multiply(FromDouble(pointerY - viewportHeight / 2d), digits).Divide(height, digits);
        var anchorShiftFactor = ArbitraryDecimal.One.Subtract(factor, digits);
        return viewport with
        {
            CenterX = ArbitraryDecimal.Parse(viewport.CenterX)
                .Add(xOffset.Multiply(anchorShiftFactor, digits), digits).ToString(),
            CenterY = ArbitraryDecimal.Parse(viewport.CenterY)
                .Add(yOffset.Multiply(anchorShiftFactor, digits), digits).ToString(),
            Scale = scale.Multiply(factor, digits).ToString()
        };
    }

    private static ArbitraryDecimal FromDouble(double value) =>
        ArbitraryDecimal.Parse(value.ToString("R", CultureInfo.InvariantCulture));
}
