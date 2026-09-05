using FractalArtPlugin.Domain.Artwork;
using FractalArtPlugin.Domain.Rendering;
using FractalArtPlugin.Numerics;

namespace FractalArtPlugin.Domain.Fractals.Julia;

internal sealed class DoubleJuliaKernel : IJuliaKernel
{
    public string Name => "double";

    public bool CanHandle(RenderContext context) => context.NumericPrecision == NumericPrecision.Double;

    public ScalarField Generate(JuliaDefinition definition, RenderContext context, CancellationToken cancellationToken)
    {
        var values = new float[checked(context.Width * context.Height)];
        var escaped = new bool[values.Length];
        var centerX = ArbitraryDecimal.Parse(definition.CenterX).ToDouble();
        var centerY = ArbitraryDecimal.Parse(definition.CenterY).ToDouble();
        var scale = ArbitraryDecimal.Parse(definition.Scale).ToDouble();
        var constantReal = ArbitraryDecimal.Parse(definition.ConstantReal).ToDouble();
        var constantImaginary = ArbitraryDecimal.Parse(definition.ConstantImaginary).ToDouble();
        var denominator = Math.Max(1, context.Height - 1);
        var step = scale / denominator;
        var left = centerX - (context.Width - 1) * step / 2d;
        var top = centerY - scale / 2d;

        for (var y = 0; y < context.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var imaginary = top + y * step;
            var real = left;
            for (var x = 0; x < context.Width; x++, real += step)
            {
                EscapeOrbitMath.ComputeDouble(
                    real,
                    imaginary,
                    constantReal,
                    constantImaginary,
                    definition.MaxIterations,
                    context.CancellationCheckInterval,
                    cancellationToken).Write(values, escaped, y * context.Width + x, definition.MaxIterations);
            }
        }

        return ScalarField.FromOwned(context.Width, context.Height, values, escaped,
            new RenderDiagnostics(Name, context.ConfiguredPrecisionDigits, context.EffectivePrecisionDigits, 1, 0, 0));
    }
}

internal static class JuliaScalar
{
    public static void Write(float[] values, bool[] escaped, int index, int iteration, int maximum, double magnitudeSquared)
    {
        if (iteration < maximum)
        {
            escaped[index] = true;
            var magnitude = Math.Max(magnitudeSquared, 4.0000001d);
            var smooth = iteration + 1d - Math.Log2(Math.Log(Math.Sqrt(magnitude)));
            values[index] = double.IsFinite(smooth)
                ? (float)Math.Clamp(smooth / maximum, 0d, 1d)
                : (float)Math.Clamp((double)iteration / maximum, 0d, 1d);
        }
        else
        {
            values[index] = 1f;
        }
    }
}
