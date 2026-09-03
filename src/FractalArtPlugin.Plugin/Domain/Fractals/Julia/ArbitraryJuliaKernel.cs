using System.Collections.Concurrent;
using System.Numerics;
using FractalArtPlugin.Domain.Artwork;
using FractalArtPlugin.Domain.Rendering;
using FractalArtPlugin.Numerics;

namespace FractalArtPlugin.Domain.Fractals.Julia;

/// <summary>
/// 权威任意精度内核。图像按连续行块并行，每块从绝对起始坐标建立锚点，块内只写自己的数组区间；
/// 调度顺序因此不参与数值结果。像素 X 坐标只做定点加法，避免旧实现逐像素重复比例乘除。
/// </summary>
internal sealed class ArbitraryJuliaKernel : IJuliaKernel
{
    public string Name => "arbitrary-fixed";

    public bool CanHandle(RenderContext context) =>
        context.NumericPrecision == NumericPrecision.Arbitrary &&
        context.KernelPreference != JuliaKernelPreference.PerturbationExperiment;

    public ScalarField Generate(JuliaDefinition definition, RenderContext context, CancellationToken cancellationToken)
    {
        var effective = BinaryFixedPoint.ForDecimalDigits(context.EffectivePrecisionDigits);
        var configured = context.ConfiguredPrecisionDigits == context.EffectivePrecisionDigits
            ? effective
            : BinaryFixedPoint.ForDecimalDigits(context.ConfiguredPrecisionDigits);
        var frame = FrameCoordinates.Create(definition, context, effective);
        var configuredFrame = ReferenceEquals(effective, configured)
            ? frame
            : FrameCoordinates.Create(definition, context, configured);
        var values = new float[checked(context.Width * context.Height)];
        var escaped = new bool[values.Length];
        var fallbackPixels = 0;
        var chunks = Partitioner.Create(0, context.Height, context.ChunkHeight);
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = context.MaxDegreeOfParallelism
        };

        // Parallel.ForEach 只为行块调度工作，绝不为单像素创建 Task。任一异常或取消会停止领取新块，
        // 正在运行的块在行、像素和迭代三个层级按有界间隔尽快观察令牌。
        Parallel.ForEach(chunks, options, range =>
        {
            var imaginary = frame.Top + range.Item1 * frame.PixelStep;
            for (var y = range.Item1; y < range.Item2; y++, imaginary += frame.PixelStep)
            {
                options.CancellationToken.ThrowIfCancellationRequested();
                var real = frame.Left;
                for (var x = 0; x < context.Width; x++, real += frame.PixelStep)
                {
                    if (x % context.CancellationCheckInterval == 0)
                    {
                        options.CancellationToken.ThrowIfCancellationRequested();
                    }

                    var sample = ComputePixel(
                        effective,
                        real,
                        imaginary,
                        frame.ConstantReal,
                        frame.ConstantImaginary,
                        definition.MaxIterations,
                        context.CancellationCheckInterval,
                        options.CancellationToken);

                    // 只有有效精度低于配置上限且结果落入逃逸阈值保护区时才逐像素提升精度。
                    // 这条回退是正确性机制，并以诊断计数公开，而不是静默接受边界结果。
                    if (!ReferenceEquals(effective, configured) && sample.NearEscapeBoundary)
                    {
                        var configuredReal = configuredFrame.Left + x * configuredFrame.PixelStep;
                        var configuredImaginary = configuredFrame.Top + y * configuredFrame.PixelStep;
                        sample = ComputePixel(
                            configured,
                            configuredReal,
                            configuredImaginary,
                            configuredFrame.ConstantReal,
                            configuredFrame.ConstantImaginary,
                            definition.MaxIterations,
                            context.CancellationCheckInterval,
                            options.CancellationToken);
                        Interlocked.Increment(ref fallbackPixels);
                    }

                    sample.Write(values, escaped, y * context.Width + x, definition.MaxIterations);
                }
            }
        });

        return new ScalarField(context.Width, context.Height, values, escaped,
            new RenderDiagnostics(
                Name,
                context.ConfiguredPrecisionDigits,
                context.EffectivePrecisionDigits,
                context.MaxDegreeOfParallelism,
                fallbackPixels,
                0));
    }

    internal static PixelSample ComputePixel(
        BinaryFixedPoint fixedPoint,
        BigInteger real,
        BigInteger imaginary,
        BigInteger constantReal,
        BigInteger constantImaginary,
        int maximumIterations,
        int cancellationCheckInterval,
        CancellationToken cancellationToken)
    {
        var zr = real;
        var zi = imaginary;
        var iteration = 0;
        var magnitudeSquared = BigInteger.Zero;
        while (iteration < maximumIterations)
        {
            if (iteration % cancellationCheckInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var zrSquared = fixedPoint.Multiply(zr, zr);
            var ziSquared = fixedPoint.Multiply(zi, zi);
            magnitudeSquared = zrSquared + ziSquared;
            if (magnitudeSquared > fixedPoint.Four)
            {
                break;
            }

            var nextReal = zrSquared - ziSquared + constantReal;
            zi = (fixedPoint.Multiply(zr, zi) << 1) + constantImaginary;
            zr = nextReal;
            iteration++;
        }

        // 保护区随有效二进制位缩小，但不小于 2^-48；它只触发更高精度复核，
        // 不直接改变逃逸分类，因此不会把诊断启发式变成作品语义。
        var guardShift = Math.Min(fixedPoint.FractionalBits, 48);
        var guard = BigInteger.One << Math.Max(0, fixedPoint.FractionalBits - guardShift);
        var nearBoundary = BigInteger.Abs(magnitudeSquared - fixedPoint.Four) <= guard;
        return new PixelSample(iteration, fixedPoint.ToDouble(magnitudeSquared), nearBoundary);
    }

    internal readonly record struct PixelSample(int Iteration, double MagnitudeSquared, bool NearEscapeBoundary)
    {
        public void Write(float[] values, bool[] escaped, int index, int maximumIterations) =>
            JuliaScalar.Write(values, escaped, index, Iteration, maximumIterations, MagnitudeSquared);
    }

    internal sealed record FrameCoordinates(
        BigInteger Left,
        BigInteger Top,
        BigInteger PixelStep,
        BigInteger ConstantReal,
        BigInteger ConstantImaginary)
    {
        public static FrameCoordinates Create(
            JuliaDefinition definition,
            RenderContext context,
            BinaryFixedPoint fixedPoint)
        {
            var centerX = fixedPoint.Parse(definition.CenterX);
            var centerY = fixedPoint.Parse(definition.CenterY);
            var scale = fixedPoint.Parse(definition.Scale);
            var denominator = Math.Max(1, context.Height - 1);
            var pixelStep = BinaryFixedPoint.DivideRounded(scale, denominator);
            var leftOffset = BinaryFixedPoint.DivideRounded(pixelStep * (context.Width - 1), 2);
            var topOffset = BinaryFixedPoint.DivideRounded(scale, 2);
            return new FrameCoordinates(
                centerX - leftOffset,
                centerY - topOffset,
                pixelStep,
                fixedPoint.Parse(definition.ConstantReal),
                fixedPoint.Parse(definition.ConstantImaginary));
        }
    }
}
