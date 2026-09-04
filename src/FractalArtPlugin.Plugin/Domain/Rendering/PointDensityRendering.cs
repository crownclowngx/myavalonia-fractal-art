namespace FractalArtPlugin.Domain.Rendering;

/// <summary>
/// 将公式空间点云确定性地累积为归一化密度场。共享直方图只接受整数原子加法，避免浮点累加顺序随线程
/// 调度变化；8 位定点双线性权重兼顾平滑落点与可重复性，单点四个权重之和固定为 256。
/// </summary>
internal sealed class PointDensityRenderer : IPointDensityRenderer
{
    private const int WeightScale = 256;
    private const int ParallelBatchSize = 16_384;

    public async Task<ScalarField> RenderAsync(
        PointCloud cloud,
        StrangeAttractorDefinition definition,
        RenderContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cloud);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(context);
        var histogram = new int[checked(context.Width * context.Height)];
        var transform = CreateTransform(cloud, context.Width, context.Height);
        var batchCount = (cloud.Points.Count + ParallelBatchSize - 1) / ParallelBatchSize;
        var batches = Enumerable.Range(0, batchCount)
            .Select(batch => (
                Start: batch * ParallelBatchSize,
                End: Math.Min(cloud.Points.Count, (batch + 1) * ParallelBatchSize)))
            .ToArray();
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, context.MaxDegreeOfParallelism),
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(batches, options, (range, token) =>
        {
            var points = cloud.Points.Span;
            for (var index = range.Start; index < range.End; index++)
            {
                if ((index & 1023) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }

                Accumulate(histogram, context.Width, context.Height, transform.Map(points[index]));
            }

            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        var maximum = histogram.Max();
        var values = new float[histogram.Length];
        var occupied = new bool[histogram.Length];
        if (maximum > 0)
        {
            var denominator = Math.Log(1d + maximum * definition.Exposure);
            var inverseGamma = 1d / definition.Gamma;
            for (var index = 0; index < histogram.Length; index++)
            {
                if ((index & 4095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var count = histogram[index];
                if (count == 0)
                {
                    continue;
                }

                occupied[index] = true;
                var exposed = Math.Log(1d + count * definition.Exposure) / denominator;
                values[index] = (float)Math.Clamp(Math.Pow(exposed, inverseGamma), 0, 1);
            }
        }

        return ScalarField.FromOwned(
            context.Width,
            context.Height,
            values,
            occupied,
            new RenderDiagnostics("attractor-density", 0, 0, context.MaxDegreeOfParallelism, 0, 0));
    }

    private static DensityTransform CreateTransform(PointCloud cloud, int width, int height)
    {
        var centerX = (cloud.MinimumX + cloud.MaximumX) / 2d;
        var centerY = (cloud.MinimumY + cloud.MaximumY) / 2d;
        // 退化轴采用“以边界中心为中心的一单位跨度”，而不是把极小浮点误差放大到整张画布。
        // 这既满足单点/直线点云的稳定取景，也让另一个非退化轴继续参与统一的等比缩放。
        var spanX = cloud.MaximumX > cloud.MinimumX ? cloud.MaximumX - cloud.MinimumX : 1d;
        var spanY = cloud.MaximumY > cloud.MinimumY ? cloud.MaximumY - cloud.MinimumY : 1d;
        var usableWidth = Math.Max(1d, (width - 1) * 0.9d);
        var usableHeight = Math.Max(1d, (height - 1) * 0.9d);
        var scale = Math.Min(usableWidth / spanX, usableHeight / spanY);
        return new DensityTransform(centerX, centerY, scale, (width - 1) / 2d, (height - 1) / 2d);
    }

    private static void Accumulate(int[] histogram, int width, int height, (double X, double Y) point)
    {
        var x0 = Math.Clamp((int)Math.Floor(point.X), 0, width - 1);
        var y0 = Math.Clamp((int)Math.Floor(point.Y), 0, height - 1);
        var x1 = Math.Min(x0 + 1, width - 1);
        var y1 = Math.Min(y0 + 1, height - 1);
        var wx1 = Math.Clamp((int)Math.Round((point.X - Math.Floor(point.X)) * WeightScale), 0, WeightScale);
        var wy1 = Math.Clamp((int)Math.Round((point.Y - Math.Floor(point.Y)) * WeightScale), 0, WeightScale);
        var wx0 = WeightScale - wx1;
        var wy0 = WeightScale - wy1;
        var w00 = wx0 * wy0 / WeightScale;
        var w10 = wx1 * wy0 / WeightScale;
        var w01 = wx0 * wy1 / WeightScale;
        var w11 = WeightScale - w00 - w10 - w01;

        if (w00 > 0) Interlocked.Add(ref histogram[y0 * width + x0], w00);
        if (w10 > 0) Interlocked.Add(ref histogram[y0 * width + x1], w10);
        if (w01 > 0) Interlocked.Add(ref histogram[y1 * width + x0], w01);
        if (w11 > 0) Interlocked.Add(ref histogram[y1 * width + x1], w11);
    }

    private readonly record struct DensityTransform(
        double CenterX,
        double CenterY,
        double Scale,
        double TargetCenterX,
        double TargetCenterY)
    {
        public (double X, double Y) Map(PointSample point) =>
            (TargetCenterX + (point.X - CenterX) * Scale,
             TargetCenterY - (point.Y - CenterY) * Scale);
    }
}

/// <summary>
/// 密度着色与逃逸时间着色分离：零密度必须透明，非零密度以自身值同时驱动渐变和 Alpha，
/// 从而吸引子可以作为真正的独立图层叠加，而不会用“内部颜色”填满整幅画布。
/// </summary>
internal sealed class DensityGradientMapper : IDensityGradientMapper
{
    public ImageSurface Map(ScalarField field, GradientDefinition gradient, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(gradient);
        var pixels = new byte[checked(field.Width * field.Height * 4)];
        var values = field.Values.Span;
        var occupied = field.Escaped.Span;
        for (var index = 0; index < values.Length; index++)
        {
            if ((index & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (!occupied[index] || values[index] <= 0)
            {
                continue;
            }

            var amount = Math.Clamp(values[index], 0, 1);
            var offset = index * 4;
            pixels[offset] = Blend(gradient.Start.Red, gradient.End.Red, amount);
            pixels[offset + 1] = Blend(gradient.Start.Green, gradient.End.Green, amount);
            pixels[offset + 2] = Blend(gradient.Start.Blue, gradient.End.Blue, amount);
            var alpha = Blend(gradient.Start.Alpha, gradient.End.Alpha, amount) * amount;
            pixels[offset + 3] = ToByte(alpha);
        }

        return ImageSurface.FromOwned(field.Width, field.Height, pixels, field.Diagnostics);
    }

    private static byte Blend(byte start, byte end, double amount) =>
        ToByte(start + (end - start) * amount);

    private static byte ToByte(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
}

/// <summary>
/// 图层局部发光对预乘 Alpha 的 RGBA 分量执行可分离高斯卷积，再把原图 source-over 到模糊光晕上。
/// 与既有 Master Bloom 分开实现，避免吸引子预设意外改变其它图层，也保持 G0008 输出指纹不变。
/// </summary>
internal sealed class DensityGlowRenderer : IDensityGlowRenderer
{
    public ImageSurface Apply(
        ImageSurface source,
        StrangeAttractorDefinition definition,
        CancellationToken cancellationToken)
    {
        if (!definition.GlowEnabled || definition.GlowStrength <= 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return source;
        }

        var premultiplied = Premultiply(source, cancellationToken);
        var kernel = CreateKernel(definition.GlowSigma);
        var horizontal = Convolve(premultiplied, source.Width, source.Height, kernel, horizontal: true, cancellationToken);
        var blurred = Convolve(horizontal, source.Width, source.Height, kernel, horizontal: false, cancellationToken);
        var input = source.Pixels.Span;
        var output = new byte[input.Length];
        for (var offset = 0; offset < output.Length; offset += 4)
        {
            if ((offset & 16383) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var sourceAlpha = input[offset + 3] / 255d;
            var glowAlpha = Math.Clamp(blurred[offset + 3] / 255d * definition.GlowStrength, 0, 1);
            var outputAlpha = sourceAlpha + glowAlpha * (1 - sourceAlpha);
            for (var channel = 0; channel < 3; channel++)
            {
                var sourcePremultiplied = input[offset + channel] / 255d * sourceAlpha;
                var glowPremultiplied = blurred[offset + channel] / 255d * definition.GlowStrength;
                var total = sourcePremultiplied + glowPremultiplied * (1 - sourceAlpha);
                output[offset + channel] = outputAlpha <= 0 ? (byte)0 : ToByte(total / outputAlpha * 255);
            }

            output[offset + 3] = ToByte(outputAlpha * 255);
        }

        return ImageSurface.FromOwned(source.Width, source.Height, output, source.Diagnostics);
    }

    private static byte[] Premultiply(ImageSurface source, CancellationToken cancellationToken)
    {
        var input = source.Pixels.Span;
        var output = new byte[input.Length];
        for (var offset = 0; offset < input.Length; offset += 4)
        {
            if ((offset & 16383) == 0) cancellationToken.ThrowIfCancellationRequested();
            var alpha = input[offset + 3] / 255d;
            output[offset] = ToByte(input[offset] * alpha);
            output[offset + 1] = ToByte(input[offset + 1] * alpha);
            output[offset + 2] = ToByte(input[offset + 2] * alpha);
            output[offset + 3] = input[offset + 3];
        }

        return output;
    }

    private static double[] CreateKernel(double sigma)
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

        for (var index = 0; index < kernel.Length; index++) kernel[index] /= sum;
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
            if ((y & 7) == 0) cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                for (var channel = 0; channel < 4; channel++)
                {
                    var total = 0d;
                    for (var index = -radius; index <= radius; index++)
                    {
                        var sx = horizontal ? Math.Clamp(x + index, 0, width - 1) : x;
                        var sy = horizontal ? y : Math.Clamp(y + index, 0, height - 1);
                        total += source[(sy * width + sx) * 4 + channel] * kernel[index + radius];
                    }

                    output[offset + channel] = ToByte(total);
                }
            }
        }

        return output;
    }

    private static byte ToByte(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
}
