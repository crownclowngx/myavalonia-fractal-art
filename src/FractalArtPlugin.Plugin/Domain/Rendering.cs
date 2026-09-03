namespace FractalArtPlugin.Domain;

public enum RenderQuality
{
    Draft,
    Final
}

/// <summary>
/// 一次渲染的完整显式上下文。算法不得自行读取 UI 尺寸、系统随机数或全局版本，
/// 这样同一版本、配方和上下文才能稳定复现。
/// </summary>
public sealed record RenderContext(
    int Width,
    int Height,
    RenderQuality Quality,
    long Seed,
    int RendererVersion)
{
    public const int CurrentRendererVersion = 1;

    public static RenderContext ForPreview(ArtworkDefinition artwork)
    {
        ArgumentNullException.ThrowIfNull(artwork);
        var maximum = artwork.Presentation.HighQualityPreview ? 960 : 480;
        var ratio = Math.Min(1d, Math.Min((double)maximum / artwork.Canvas.Width, (double)maximum / artwork.Canvas.Height));
        return new RenderContext(
            Math.Max(1, (int)Math.Round(artwork.Canvas.Width * ratio)),
            Math.Max(1, (int)Math.Round(artwork.Canvas.Height * ratio)),
            RenderQuality.Draft,
            artwork.Seed,
            CurrentRendererVersion);
    }

    public static RenderContext ForExport(ArtworkDefinition artwork) => new(
        artwork.Canvas.Width,
        artwork.Canvas.Height,
        RenderQuality.Final,
        artwork.Seed,
        CurrentRendererVersion);
}

/// <summary>归一化迭代标量场；Escaped 单独保存，避免把内部点与低迭代值混淆。</summary>
public sealed class ScalarField
{
    public ScalarField(int width, int height, float[] values, bool[] escaped)
    {
        if (width <= 0 || height <= 0 || values.Length != width * height || escaped.Length != values.Length)
        {
            throw new ArgumentException("标量场尺寸与数据长度不一致。");
        }

        Width = width;
        Height = height;
        Values = values;
        Escaped = escaped;
    }

    public int Width { get; }
    public int Height { get; }
    public float[] Values { get; }
    public bool[] Escaped { get; }
}

/// <summary>与 UI 框架无关的 RGBA8888 图像面；导出与预览共享这一结果。</summary>
public sealed class RgbaImage
{
    public RgbaImage(int width, int height, byte[] pixels)
    {
        if (width <= 0 || height <= 0 || pixels.Length != checked(width * height * 4))
        {
            throw new ArgumentException("RGBA 图像尺寸与像素长度不一致。");
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
}

public interface IJuliaFieldGenerator
{
    Task<ScalarField> GenerateAsync(JuliaDefinition definition, RenderContext context, CancellationToken cancellationToken);
}

public interface IGradientMapper
{
    RgbaImage Map(ScalarField field, GradientDefinition gradient, CancellationToken cancellationToken);
}

internal sealed class JuliaFieldGenerator : IJuliaFieldGenerator
{
    public Task<ScalarField> GenerateAsync(
        JuliaDefinition definition,
        RenderContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (context.RendererVersion != RenderContext.CurrentRendererVersion)
        {
            throw new NotSupportedException($"不支持渲染器版本 {context.RendererVersion}。");
        }

        return Task.Run(() => Generate(definition, context, cancellationToken), cancellationToken);
    }

    private static ScalarField Generate(
        JuliaDefinition definition,
        RenderContext context,
        CancellationToken cancellationToken)
    {
        var values = new float[checked(context.Width * context.Height)];
        var escaped = new bool[values.Length];
        var aspect = (double)context.Width / context.Height;

        for (var y = 0; y < context.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var imaginary = definition.CenterY + ((double)y / Math.Max(1, context.Height - 1) - 0.5) * definition.Scale;
            for (var x = 0; x < context.Width; x++)
            {
                var real = definition.CenterX + ((double)x / Math.Max(1, context.Width - 1) - 0.5) * definition.Scale * aspect;
                var zr = real;
                var zi = imaginary;
                var iteration = 0;
                while (iteration < definition.MaxIterations && zr * zr + zi * zi <= 4d)
                {
                    var nextReal = zr * zr - zi * zi + definition.ConstantReal;
                    zi = 2d * zr * zi + definition.ConstantImaginary;
                    zr = nextReal;
                    iteration++;
                }

                var index = y * context.Width + x;
                if (iteration < definition.MaxIterations)
                {
                    escaped[index] = true;
                    // 平滑迭代值可显著减少色带；最终仍夹到 0..1，满足标量场契约。
                    var magnitudeSquared = Math.Max(zr * zr + zi * zi, 4.0000001d);
                    var smooth = iteration + 1d - Math.Log2(Math.Log(Math.Sqrt(magnitudeSquared)));
                    values[index] = (float)Math.Clamp(smooth / definition.MaxIterations, 0d, 1d);
                }
                else
                {
                    values[index] = 1f;
                }
            }
        }

        return new ScalarField(context.Width, context.Height, values, escaped);
    }
}

internal sealed class LinearGradientMapper : IGradientMapper
{
    public RgbaImage Map(ScalarField field, GradientDefinition gradient, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(gradient);
        var pixels = new byte[checked(field.Width * field.Height * 4)];
        for (var index = 0; index < field.Values.Length; index++)
        {
            if ((index & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var color = field.Escaped[index]
                ? Interpolate(gradient.Start, gradient.End, field.Values[index])
                : gradient.Interior;
            var offset = index * 4;
            pixels[offset] = color.Red;
            pixels[offset + 1] = color.Green;
            pixels[offset + 2] = color.Blue;
            pixels[offset + 3] = color.Alpha;
        }

        return new RgbaImage(field.Width, field.Height, pixels);
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
