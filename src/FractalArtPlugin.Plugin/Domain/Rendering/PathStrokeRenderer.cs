using FractalArtPlugin.Domain.Artwork;

namespace FractalArtPlugin.Domain.Rendering;

/// <summary>
/// 路径到 RGBA 图像面的 CPU 描边器。当前采用带圆形端帽的确定性采样，设计目标是让预览与 PNG
/// 共用同一结果；路径本身仍独立保留，因此这一步不是生成器的隐式栅格化。
/// </summary>
internal sealed class PathStrokeRenderer : IPathStrokeRenderer
{
    public RgbaImage Render(
        PathGeometry geometry,
        RecursiveTreeDefinition definition,
        GradientDefinition gradient,
        RgbaColor background,
        RenderContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var pixels = CreateBackground(context.Width, context.Height, background);
        var scale = Math.Min(context.Width, context.Height) / 800d;

        for (var index = 0; index < geometry.Segments.Count; index++)
        {
            if ((index & 127) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var segment = geometry.Segments[index];
            var levelAmount = geometry.MaximumLevel == 0 ? 0d : segment.Level / (double)geometry.MaximumLevel;
            var color = Interpolate(gradient.Start, gradient.End, levelAmount);
            var width = Math.Max(0.65, definition.StrokeWidth * scale * Math.Pow(0.82, segment.Level));
            DrawSegment(pixels, context.Width, context.Height, segment, width, color);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new RgbaImage(
            context.Width,
            context.Height,
            pixels,
            new RenderDiagnostics("recursive-tree", 0, 0, 1, 0, 0));
    }

    private static byte[] CreateBackground(int width, int height, RgbaColor color)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = color.Red;
            pixels[offset + 1] = color.Green;
            pixels[offset + 2] = color.Blue;
            pixels[offset + 3] = color.Alpha;
        }

        return pixels;
    }

    private static void DrawSegment(
        byte[] pixels,
        int width,
        int height,
        PathSegment segment,
        double strokeWidth,
        RgbaColor color)
    {
        // 几何位于归一化方形逻辑画板；使用短边等比映射并在长边居中，保证“30° 分叉”
        // 在宽画布、方画布和竖画布中仍是同一个角度，不被各轴独立缩放成另一种形态。
        var artboardSize = Math.Min(width - 1d, height - 1d);
        var offsetX = (width - 1d - artboardSize) / 2d;
        var offsetY = (height - 1d - artboardSize) / 2d;
        var startX = offsetX + segment.Start.X * artboardSize;
        var startY = offsetY + segment.Start.Y * artboardSize;
        var endX = offsetX + segment.End.X * artboardSize;
        var endY = offsetY + segment.End.Y * artboardSize;
        var deltaX = endX - startX;
        var deltaY = endY - startY;
        var steps = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(deltaX), Math.Abs(deltaY)) * 1.5));
        var radius = strokeWidth / 2d;
        var integerRadius = Math.Max(1, (int)Math.Ceiling(radius + 1));

        for (var step = 0; step <= steps; step++)
        {
            var amount = step / (double)steps;
            var centerX = startX + deltaX * amount;
            var centerY = startY + deltaY * amount;
            var minimumX = Math.Max(0, (int)Math.Floor(centerX) - integerRadius);
            var maximumX = Math.Min(width - 1, (int)Math.Ceiling(centerX) + integerRadius);
            var minimumY = Math.Max(0, (int)Math.Floor(centerY) - integerRadius);
            var maximumY = Math.Min(height - 1, (int)Math.Ceiling(centerY) + integerRadius);

            for (var y = minimumY; y <= maximumY; y++)
            {
                for (var x = minimumX; x <= maximumX; x++)
                {
                    var distance = Math.Sqrt(Math.Pow(x + 0.5 - centerX, 2) + Math.Pow(y + 0.5 - centerY, 2));
                    var coverage = Math.Clamp(radius + 0.75 - distance, 0, 1);
                    if (coverage > 0)
                    {
                        BlendPixel(pixels, (y * width + x) * 4, color, coverage);
                    }
                }
            }
        }
    }

    private static void BlendPixel(byte[] pixels, int offset, RgbaColor color, double coverage)
    {
        var alpha = color.Alpha / 255d * coverage;
        var inverse = 1d - alpha;
        pixels[offset] = Blend(pixels[offset], color.Red, alpha, inverse);
        pixels[offset + 1] = Blend(pixels[offset + 1], color.Green, alpha, inverse);
        pixels[offset + 2] = Blend(pixels[offset + 2], color.Blue, alpha, inverse);
        pixels[offset + 3] = (byte)Math.Clamp(
            (int)Math.Round(255d * (alpha + pixels[offset + 3] / 255d * inverse)), 0, 255);
    }

    private static byte Blend(byte background, byte foreground, double alpha, double inverse) =>
        (byte)Math.Clamp((int)Math.Round(foreground * alpha + background * inverse), 0, 255);

    private static RgbaColor Interpolate(RgbaColor start, RgbaColor end, double amount)
    {
        var value = Math.Clamp(amount, 0, 1);
        return new RgbaColor(
            Lerp(start.Red, end.Red, value),
            Lerp(start.Green, end.Green, value),
            Lerp(start.Blue, end.Blue, value),
            Lerp(start.Alpha, end.Alpha, value));
    }

    private static byte Lerp(byte start, byte end, double amount) =>
        (byte)Math.Clamp((int)Math.Round(start + (end - start) * amount), 0, 255);
}
