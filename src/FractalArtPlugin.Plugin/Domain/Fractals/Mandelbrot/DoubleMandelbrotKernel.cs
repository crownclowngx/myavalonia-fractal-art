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
                EscapeOrbitMath.ComputeDouble(
                    0,
                    0,
                    constantReal,
                    constantImaginary,
                    definition.MaxIterations,
                    context.CancellationCheckInterval,
                    cancellationToken).Write(values, escaped, y * context.Width + x, definition.MaxIterations);
            }
        }

        return ScalarField.FromOwned(context.Width, context.Height, values, escaped,
            new RenderDiagnostics("mandelbrot-double", context.ConfiguredPrecisionDigits,
                context.EffectivePrecisionDigits, 1, 0, 0));
    }
}
