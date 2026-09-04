using System.Collections.Concurrent;
using System.Numerics;
using FractalArtPlugin.Domain.Artwork;
using FractalArtPlugin.Domain.Fractals.Julia;
using FractalArtPlugin.Domain.Rendering;
using FractalArtPlugin.Numerics;

namespace FractalArtPlugin.Domain.Fractals.Mandelbrot;

/// <summary>
/// Mandelbrot 任意精度参考内核。它延续 Julia 的确定性行块并行约束，但独立实现 z₀=0、c=像素的公式，
/// 不通过条件分支污染已经稳定的 Julia 热路径。
/// </summary>
internal sealed class ArbitraryMandelbrotKernel : IMandelbrotKernel
{
    public bool CanHandle(RenderContext context) => context.NumericPrecision == NumericPrecision.Arbitrary;

    public ScalarField Generate(
        MandelbrotDefinition definition,
        RenderContext context,
        CancellationToken cancellationToken)
    {
        var fixedPoint = BinaryFixedPoint.ForDecimalDigits(context.EffectivePrecisionDigits);
        var centerX = fixedPoint.Parse(definition.CenterX);
        var centerY = fixedPoint.Parse(definition.CenterY);
        var scale = fixedPoint.Parse(definition.Scale);
        var step = BinaryFixedPoint.DivideRounded(scale, Math.Max(1, context.Height - 1));
        var left = centerX - BinaryFixedPoint.DivideRounded(step * (context.Width - 1), 2);
        var top = centerY - BinaryFixedPoint.DivideRounded(scale, 2);
        var values = new float[checked(context.Width * context.Height)];
        var escaped = new bool[values.Length];
        var chunks = Partitioner.Create(0, context.Height, context.ChunkHeight);
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = context.MaxDegreeOfParallelism
        };

        Parallel.ForEach(chunks, options, range =>
        {
            var constantImaginary = top + range.Item1 * step;
            for (var y = range.Item1; y < range.Item2; y++, constantImaginary += step)
            {
                options.CancellationToken.ThrowIfCancellationRequested();
                var constantReal = left;
                for (var x = 0; x < context.Width; x++, constantReal += step)
                {
                    if (x % context.CancellationCheckInterval == 0)
                    {
                        options.CancellationToken.ThrowIfCancellationRequested();
                    }

                    ComputePixel(
                        fixedPoint,
                        constantReal,
                        constantImaginary,
                        definition.MaxIterations,
                        context.CancellationCheckInterval,
                        options.CancellationToken,
                        values,
                        escaped,
                        y * context.Width + x);
                }
            }
        });

        return ScalarField.FromOwned(context.Width, context.Height, values, escaped,
            new RenderDiagnostics("mandelbrot-arbitrary-fixed", context.ConfiguredPrecisionDigits,
                context.EffectivePrecisionDigits, context.MaxDegreeOfParallelism, 0, 0));
    }

    private static void ComputePixel(
        BinaryFixedPoint fixedPoint,
        BigInteger constantReal,
        BigInteger constantImaginary,
        int maximumIterations,
        int cancellationCheckInterval,
        CancellationToken cancellationToken,
        float[] values,
        bool[] escaped,
        int index)
    {
        var real = BigInteger.Zero;
        var imaginary = BigInteger.Zero;
        var magnitudeSquared = BigInteger.Zero;
        var iteration = 0;
        while (iteration < maximumIterations)
        {
            if (iteration % cancellationCheckInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var realSquared = fixedPoint.Multiply(real, real);
            var imaginarySquared = fixedPoint.Multiply(imaginary, imaginary);
            magnitudeSquared = realSquared + imaginarySquared;
            if (magnitudeSquared > fixedPoint.Four)
            {
                break;
            }

            var nextReal = realSquared - imaginarySquared + constantReal;
            imaginary = (fixedPoint.Multiply(real, imaginary) << 1) + constantImaginary;
            real = nextReal;
            iteration++;
        }

        JuliaScalar.Write(values, escaped, index, iteration, maximumIterations, fixedPoint.ToDouble(magnitudeSquared));
    }
}
