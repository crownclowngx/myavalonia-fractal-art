using FractalArtPlugin.Domain.Artwork;

namespace FractalArtPlugin.Domain.Rendering;

internal sealed class LinearGradientMapper : IGradientMapper
{
    public ImageSurface Map(ScalarField field, GradientDefinition gradient, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(gradient);
        var pixels = new byte[checked(field.Width * field.Height * 4)];
        var values = field.Values.Span;
        var escaped = field.Escaped.Span;
        for (var index = 0; index < values.Length; index++)
        {
            if ((index & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var color = escaped[index]
                ? Interpolate(gradient.Start, gradient.End, values[index])
                : gradient.Interior;
            var offset = index * 4;
            pixels[offset] = color.Red;
            pixels[offset + 1] = color.Green;
            pixels[offset + 2] = color.Blue;
            pixels[offset + 3] = color.Alpha;
        }

        return ImageSurface.FromOwned(field.Width, field.Height, pixels, field.Diagnostics);
    }

    private static RgbaColor Interpolate(RgbaColor start, RgbaColor end, float amount)
    {
        var value = Math.Clamp(amount, 0f, 1f);
        return new RgbaColor(
            Blend(start.Red, end.Red, value),
            Blend(start.Green, end.Green, value),
            Blend(start.Blue, end.Blue, value),
            Blend(start.Alpha, end.Alpha, value));
    }

    private static byte Blend(byte start, byte end, float amount) =>
        (byte)Math.Clamp((int)Math.Round(start + (end - start) * amount), 0, byte.MaxValue);
}
