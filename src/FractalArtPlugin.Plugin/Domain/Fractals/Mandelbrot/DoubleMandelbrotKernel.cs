using FractalArtPlugin.Domain.Artwork;
using FractalArtPlugin.Domain.Fractals.Julia;
using FractalArtPlugin.Domain.Rendering;
using FractalArtPlugin.Numerics;

namespace FractalArtPlugin.Domain.Fractals.Mandelbrot;

internal sealed class DoubleMandelbrotKernel : IMandelbrotKernel
{
    public bool CanHandle(RenderContext context) => context.NumericPrecision == NumericPrecision.Double;

    public ScalarField Generate(
        MandelbrotDefinition definition,
        RenderContext context,
        CancellationToken cancellationToken)
    {
        var values = new float[checked(context.Width * context.Height)];
        var escaped = new bool[values.Length];
        var centerX = ArbitraryDecimal.Parse(definition.CenterX).ToDouble();
        var centerY = ArbitraryDecimal.Parse(definition.CenterY).ToDouble();
        var scale = ArbitraryDecimal.Parse(definition.Scale).ToDouble();
        var denominator = Math.Max(1, context.Height - 1);
        var step = scale / denominator;
        var left = centerX - (context.Width - 1) * step / 2d;
        var top = centerY - scale / 2d;

        for (var y = 0; y < context.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var constantImaginary = top + y * step;
            var constantReal = left;
            for (var x = 0; x < context.Width; x++, constantReal += step)
            {
                var real = 0d;
                var imaginary = 0d;
                var iteration = 0;
                while (iteration < definition.MaxIterations && real * real + imaginary * imaginary <= 4d)
                {
                    if (iteration % context.CancellationCheckInterval == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    var nextReal = real * real - imaginary * imaginary + constantReal;
                    imaginary = 2d * real * imaginary + constantImaginary;
                    real = nextReal;
                    iteration++;
                }

                JuliaScalar.Write(
                    values,
                    escaped,
                    y * context.Width + x,
                    iteration,
                    definition.MaxIterations,
                    real * real + imaginary * imaginary);
            }
        }

        return ScalarField.FromOwned(context.Width, context.Height, values, escaped,
            new RenderDiagnostics("mandelbrot-double", context.ConfiguredPrecisionDigits,
                context.EffectivePrecisionDigits, 1, 0, 0));
    }
}
