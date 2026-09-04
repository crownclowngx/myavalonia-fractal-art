using FractalArtPlugin.Domain.Artwork;

namespace FractalArtPlugin.Domain.Rendering;

public interface IScalarMaskConverter
{
    Mask Convert(ScalarField field, ScalarMaskDefinition definition, CancellationToken cancellationToken);
}

public interface ILayerRasterTransformer
{
    ImageSurface Transform(ImageSurface source, LayerTransformDefinition transform, CancellationToken cancellationToken);
    Mask Transform(Mask source, LayerTransformDefinition transform, CancellationToken cancellationToken);
}

public interface ILayerCompositor
{
    ImageSurface CreateBackground(int width, int height, RgbaColor color);
    ImageSurface CreateTransparent(int width, int height);
    ImageSurface Composite(
        ImageSurface backdrop,
        ImageSurface source,
        double opacity,
        LayerBlendMode blendMode,
        Mask? mask,
        CancellationToken cancellationToken);
}

public interface IMasterEffectRenderer
{
    ImageSurface Apply(ImageSurface source, EffectChainDefinition effects, CancellationToken cancellationToken);
}

internal sealed class ScalarMaskConverter : IScalarMaskConverter
{
    public Mask Convert(ScalarField field, ScalarMaskDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(definition);
        var values = field.Values.Span;
        var escaped = field.Escaped.Span;
        var result = new byte[values.Length];
        var half = definition.Softness / 2d;
        var low = definition.Threshold - half;
        var high = definition.Threshold + half;
        for (var index = 0; index < result.Length; index++)
        {
            if ((index & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var value = escaped[index] ? Math.Clamp(values[index], 0f, 1f) : 0d;
            double amount;
            if (definition.Softness <= 0)
            {
                amount = value >= definition.Threshold ? 1 : 0;
            }
            else
            {
                var normalized = Math.Clamp((value - low) / (high - low), 0, 1);
                amount = normalized * normalized * (3 - 2 * normalized);
            }

            if (definition.IsInverted)
            {
                amount = 1 - amount;
            }

            result[index] = ToByte(amount * 255);
        }

        return new Mask(field.Width, field.Height, result);
    }

    private static byte ToByte(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
}

internal sealed class LayerRasterTransformer : ILayerRasterTransformer
{
    public ImageSurface Transform(
        ImageSurface source,
        LayerTransformDefinition transform,
        CancellationToken cancellationToken)
    {
        if (transform == LayerTransformDefinition.Identity)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return source;
        }

        var output = new byte[checked(source.Width * source.Height * 4)];
        var input = source.Pixels.Span;
        for (var y = 0; y < source.Height; y++)
        {
            if ((y & 15) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            for (var x = 0; x < source.Width; x++)
            {
                var point = InverseMap(x + 0.5, y + 0.5, source.Width, source.Height, transform);
                SamplePremultiplied(input, source.Width, source.Height, point.X - 0.5, point.Y - 0.5,
                    output, (y * source.Width + x) * 4);
            }
        }

        return ImageSurface.FromOwned(source.Width, source.Height, output, source.Diagnostics);
    }

    public Mask Transform(Mask source, LayerTransformDefinition transform, CancellationToken cancellationToken)
    {
        if (transform == LayerTransformDefinition.Identity)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return source;
        }

        var output = new byte[checked(source.Width * source.Height)];
        var input = source.Values.Span;
        for (var y = 0; y < source.Height; y++)
        {
            if ((y & 31) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            for (var x = 0; x < source.Width; x++)
            {
                var point = InverseMap(x + 0.5, y + 0.5, source.Width, source.Height, transform);
                output[y * source.Width + x] = SampleByte(input, source.Width, source.Height, point.X - 0.5, point.Y - 0.5);
            }
        }

        return new Mask(source.Width, source.Height, output);
    }

    /// <summary>
    /// 对目标像素应用逆变换，避免正向投影留下空洞。屏幕 Y 轴向下，所以数学上的负角度对应用户看到的顺时针正角度。
    /// </summary>
    private static (double X, double Y) InverseMap(
        double x,
        double y,
        int width,
        int height,
        LayerTransformDefinition transform)
    {
        var anchorX = width * transform.AnchorXPercent / 100d;
        var anchorY = height * transform.AnchorYPercent / 100d;
        var translatedX = x - anchorX - width * transform.PositionXPercent / 100d;
        var translatedY = y - anchorY - height * transform.PositionYPercent / 100d;
        var radians = -transform.RotationDegrees * Math.PI / 180d;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var scale = transform.ScalePercent / 100d;
        return (
            (translatedX * cosine - translatedY * sine) / scale + anchorX,
            (translatedX * sine + translatedY * cosine) / scale + anchorY);
    }

    private static void SamplePremultiplied(
        ReadOnlySpan<byte> input,
        int width,
        int height,
        double x,
        double y,
        byte[] output,
        int outputOffset)
    {
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var fx = x - x0;
        var fy = y - y0;
        Span<double> premultiplied = stackalloc double[4];
        for (var dy = 0; dy <= 1; dy++)
        {
            for (var dx = 0; dx <= 1; dx++)
            {
                var sx = x0 + dx;
                var sy = y0 + dy;
                if (sx < 0 || sx >= width || sy < 0 || sy >= height)
                {
                    continue;
                }

                var weight = (dx == 0 ? 1 - fx : fx) * (dy == 0 ? 1 - fy : fy);
                var offset = (sy * width + sx) * 4;
                var alpha = input[offset + 3] / 255d;
                premultiplied[0] += input[offset] / 255d * alpha * weight;
                premultiplied[1] += input[offset + 1] / 255d * alpha * weight;
                premultiplied[2] += input[offset + 2] / 255d * alpha * weight;
                premultiplied[3] += alpha * weight;
            }
        }

        var outputAlpha = Math.Clamp(premultiplied[3], 0, 1);
        if (outputAlpha > 0)
        {
            output[outputOffset] = ToByte(premultiplied[0] / outputAlpha * 255);
            output[outputOffset + 1] = ToByte(premultiplied[1] / outputAlpha * 255);
            output[outputOffset + 2] = ToByte(premultiplied[2] / outputAlpha * 255);
        }

        output[outputOffset + 3] = ToByte(outputAlpha * 255);
    }

    private static byte SampleByte(ReadOnlySpan<byte> input, int width, int height, double x, double y)
    {
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var fx = x - x0;
        var fy = y - y0;
        var value = 0d;
        for (var dy = 0; dy <= 1; dy++)
        {
            for (var dx = 0; dx <= 1; dx++)
            {
                var sx = x0 + dx;
                var sy = y0 + dy;
                if (sx < 0 || sx >= width || sy < 0 || sy >= height)
                {
                    continue;
                }

                var weight = (dx == 0 ? 1 - fx : fx) * (dy == 0 ? 1 - fy : fy);
                value += input[sy * width + sx] * weight;
            }
        }

        return ToByte(value);
    }

    private static byte ToByte(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
}

internal sealed class LayerCompositor : ILayerCompositor
{
    public ImageSurface CreateBackground(int width, int height, RgbaColor color)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = color.Red;
            pixels[offset + 1] = color.Green;
            pixels[offset + 2] = color.Blue;
            pixels[offset + 3] = color.Alpha;
        }

        return ImageSurface.FromOwned(width, height, pixels);
    }

    public ImageSurface CreateTransparent(int width, int height) =>
        ImageSurface.FromOwned(width, height, new byte[checked(width * height * 4)]);

    public ImageSurface Composite(
        ImageSurface backdrop,
        ImageSurface source,
        double opacity,
        LayerBlendMode blendMode,
        Mask? mask,
        CancellationToken cancellationToken)
    {
        if (backdrop.Width != source.Width || backdrop.Height != source.Height ||
            mask is not null && (mask.Width != source.Width || mask.Height != source.Height))
        {
            throw new ArgumentException("参与合成的图像与遮罩尺寸必须一致。");
        }

        var destination = backdrop.Pixels.ToArray();
        var foreground = source.Pixels.Span;
        var maskValues = mask is null ? default : mask.Values.Span;
        for (var pixel = 0; pixel < backdrop.Width * backdrop.Height; pixel++)
        {
            if ((pixel & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var offset = pixel * 4;
            var sourceAlpha = foreground[offset + 3] / 255d * opacity *
                (mask is null ? 1 : maskValues[pixel] / 255d);
            var backdropAlpha = destination[offset + 3] / 255d;
            var outputAlpha = sourceAlpha + backdropAlpha * (1 - sourceAlpha);
            if (outputAlpha <= 0)
            {
                destination.AsSpan(offset, 4).Clear();
                continue;
            }

            for (var channel = 0; channel < 3; channel++)
            {
                var cb = destination[offset + channel] / 255d;
                var cs = foreground[offset + channel] / 255d;
                var blended = Blend(cb, cs, blendMode);
                var premultiplied = sourceAlpha * (1 - backdropAlpha) * cs +
                                    sourceAlpha * backdropAlpha * blended +
                                    (1 - sourceAlpha) * backdropAlpha * cb;
                destination[offset + channel] = ToByte(premultiplied / outputAlpha * 255);
            }

            destination[offset + 3] = ToByte(outputAlpha * 255);
        }

        return ImageSurface.FromOwned(backdrop.Width, backdrop.Height, destination, source.Diagnostics ?? backdrop.Diagnostics);
    }

    private static double Blend(double backdrop, double source, LayerBlendMode mode) => mode switch
    {
        LayerBlendMode.Normal => source,
        LayerBlendMode.Multiply => backdrop * source,
        LayerBlendMode.Screen => 1 - (1 - backdrop) * (1 - source),
        LayerBlendMode.Add => Math.Min(1, backdrop + source),
        LayerBlendMode.Overlay => backdrop <= 0.5
            ? 2 * backdrop * source
            : 1 - 2 * (1 - backdrop) * (1 - source),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "未知混合模式。")
    };

    private static byte ToByte(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
}

internal sealed class MasterEffectRenderer : IMasterEffectRenderer
{
    public ImageSurface Apply(ImageSurface source, EffectChainDefinition effects, CancellationToken cancellationToken)
    {
        var current = source;
        foreach (var effect in effects.Effects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            current = effect switch
            {
                ToneEffectDefinition { IsEnabled: true } tone => ApplyTone(current, tone, cancellationToken),
                BloomEffectDefinition { IsEnabled: true } bloom => ApplyBloom(current, bloom, cancellationToken),
                _ => current
            };
        }

        return current;
    }

    private static ImageSurface ApplyTone(
        ImageSurface source,
        ToneEffectDefinition effect,
        CancellationToken cancellationToken)
    {
        var pixels = source.Pixels.ToArray();
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            if ((offset & 16383) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            // 完全透明像素的 RGB 没有可见意义。保持为零可避免正亮度把透明区写成“隐藏颜色”，
            // 随后经过缩放插值或 Bloom 时又意外渗入可见边缘。
            if (pixels[offset + 3] == 0)
            {
                pixels.AsSpan(offset, 3).Clear();
                continue;
            }

            var red = pixels[offset] / 255d;
            var green = pixels[offset + 1] / 255d;
            var blue = pixels[offset + 2] / 255d;
            var luminance = red * 0.2126 + green * 0.7152 + blue * 0.0722;
            red = luminance + (red - luminance) * effect.Saturation;
            green = luminance + (green - luminance) * effect.Saturation;
            blue = luminance + (blue - luminance) * effect.Saturation;
            var contrast = 1 + effect.Contrast;
            pixels[offset] = ToByte((((red - 0.5) * contrast + 0.5) + effect.Brightness) * 255);
            pixels[offset + 1] = ToByte((((green - 0.5) * contrast + 0.5) + effect.Brightness) * 255);
            pixels[offset + 2] = ToByte((((blue - 0.5) * contrast + 0.5) + effect.Brightness) * 255);
        }

        return ImageSurface.FromOwned(source.Width, source.Height, pixels, source.Diagnostics);
    }

    private static ImageSurface ApplyBloom(
        ImageSurface source,
        BloomEffectDefinition effect,
        CancellationToken cancellationToken)
    {
        var sourcePixels = source.Pixels.Span;
        var bright = new byte[sourcePixels.Length];
        for (var offset = 0; offset < bright.Length; offset += 4)
        {
            if ((offset & 16383) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var alpha = sourcePixels[offset + 3] / 255d;
            var luminance = (sourcePixels[offset] * 0.2126 + sourcePixels[offset + 1] * 0.7152 +
                             sourcePixels[offset + 2] * 0.0722) / 255d * alpha;
            if (luminance >= effect.Threshold)
            {
                bright[offset] = ToByte(sourcePixels[offset] * alpha);
                bright[offset + 1] = ToByte(sourcePixels[offset + 1] * alpha);
                bright[offset + 2] = ToByte(sourcePixels[offset + 2] * alpha);
            }

            bright[offset + 3] = sourcePixels[offset + 3];
        }

        var kernel = CreateGaussianKernel(effect.Sigma);
        var horizontal = Convolve(bright, source.Width, source.Height, kernel, true, cancellationToken);
        var blurred = Convolve(horizontal, source.Width, source.Height, kernel, false, cancellationToken);
        var output = source.Pixels.ToArray();
        for (var offset = 0; offset < output.Length; offset += 4)
        {
            if ((offset & 16383) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            output[offset] = ToByte(output[offset] + blurred[offset] * effect.Strength);
            output[offset + 1] = ToByte(output[offset + 1] + blurred[offset + 1] * effect.Strength);
            output[offset + 2] = ToByte(output[offset + 2] + blurred[offset + 2] * effect.Strength);
            if (output[offset + 3] == 0)
            {
                output.AsSpan(offset, 3).Clear();
            }
        }

        return ImageSurface.FromOwned(source.Width, source.Height, output, source.Diagnostics);
    }

    private static double[] CreateGaussianKernel(double sigma)
    {
        var radius = Math.Min(30, Math.Max(1, (int)Math.Ceiling(3 * sigma)));
        var kernel = new double[radius * 2 + 1];
        var sum = 0d;
        for (var index = -radius; index <= radius; index++)
        {
            var value = Math.Exp(-(index * index) / (2 * sigma * sigma));
            kernel[index + radius] = value;
            sum += value;
        }

        for (var index = 0; index < kernel.Length; index++)
        {
            kernel[index] /= sum;
        }

        return kernel;
    }

    private static byte[] Convolve(
        byte[] source,
        int width,
        int height,
        double[] kernel,
        bool horizontal,
        CancellationToken cancellationToken)
    {
        var output = new byte[source.Length];
        var radius = kernel.Length / 2;
        for (var y = 0; y < height; y++)
        {
            if ((y & 7) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            for (var x = 0; x < width; x++)
            {
                var outputOffset = (y * width + x) * 4;
                for (var channel = 0; channel < 3; channel++)
                {
                    var total = 0d;
                    for (var index = -radius; index <= radius; index++)
                    {
                        var sx = horizontal ? Math.Clamp(x + index, 0, width - 1) : x;
                        var sy = horizontal ? y : Math.Clamp(y + index, 0, height - 1);
                        total += source[(sy * width + sx) * 4 + channel] * kernel[index + radius];
                    }

                    output[outputOffset + channel] = ToByte(total);
                }

                output[outputOffset + 3] = source[outputOffset + 3];
            }
        }

        return output;
    }

    private static byte ToByte(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
}
