using System.Numerics;

namespace FractalArtPlugin.Domain;

public enum RenderQuality
{
    Draft,
    Final
}

public enum NumericPrecision
{
    Double,
    Arbitrary
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
    int RendererVersion,
    NumericPrecision NumericPrecision,
    int PrecisionDigits)
{
    public const int CurrentRendererVersion = 1;

    public static RenderContext ForPreview(ArtworkDefinition artwork)
    {
        ArgumentNullException.ThrowIfNull(artwork);
        var numericPrecision = ResolveNumericPrecision(artwork.Julia);
        // 任意精度的 BigInteger 乘法成本明显高于 double，交互预览使用更保守的像素预算；
        // 最终导出仍保持规范画布，不能用低成本预览冒充成品。
        var maximum = numericPrecision == NumericPrecision.Arbitrary
            ? ResolveArbitraryPreviewBudget(artwork.Julia.PrecisionDigits, artwork.Presentation.HighQualityPreview)
            : artwork.Presentation.HighQualityPreview ? 960 : 480;
        var ratio = Math.Min(1d, Math.Min((double)maximum / artwork.Canvas.Width, (double)maximum / artwork.Canvas.Height));
        return new RenderContext(
            Math.Max(1, (int)Math.Round(artwork.Canvas.Width * ratio)),
            Math.Max(1, (int)Math.Round(artwork.Canvas.Height * ratio)),
            RenderQuality.Draft,
            artwork.Seed,
            CurrentRendererVersion,
            numericPrecision,
            artwork.Julia.PrecisionDigits);
    }

    public static RenderContext ForExport(ArtworkDefinition artwork) => new(
        artwork.Canvas.Width,
        artwork.Canvas.Height,
        RenderQuality.Final,
        artwork.Seed,
        CurrentRendererVersion,
        ResolveNumericPrecision(artwork.Julia),
        artwork.Julia.PrecisionDigits);

    private static NumericPrecision ResolveNumericPrecision(JuliaDefinition julia)
    {
        var scale = ArbitraryDecimal.Parse(julia.Scale);
        return julia.ForceHighPrecision || scale.AdjustedExponent <= -12
            ? NumericPrecision.Arbitrary
            : NumericPrecision.Double;
    }

    private static int ResolveArbitraryPreviewBudget(int precisionDigits, bool highQuality) => precisionDigits switch
    {
        <= 128 => highQuality ? 480 : 320,
        <= 256 => highQuality ? 360 : 240,
        <= 512 => highQuality ? 240 : 160,
        _ => highQuality ? 144 : 96
    };
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

        return Task.Run(
            () => context.NumericPrecision == NumericPrecision.Arbitrary
                ? GenerateArbitrary(definition, context, cancellationToken)
                : GenerateDouble(definition, context, cancellationToken),
            cancellationToken);
    }

    private static ScalarField GenerateDouble(
        JuliaDefinition definition,
        RenderContext context,
        CancellationToken cancellationToken)
    {
        var values = new float[checked(context.Width * context.Height)];
        var escaped = new bool[values.Length];
        var centerX = ArbitraryDecimal.Parse(definition.CenterX).ToDouble();
        var centerY = ArbitraryDecimal.Parse(definition.CenterY).ToDouble();
        var scale = ArbitraryDecimal.Parse(definition.Scale).ToDouble();
        var constantReal = ArbitraryDecimal.Parse(definition.ConstantReal).ToDouble();
        var constantImaginary = ArbitraryDecimal.Parse(definition.ConstantImaginary).ToDouble();
        var denominator = 2d * Math.Max(1, context.Height - 1);

        for (var y = 0; y < context.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var imaginary = centerY + (2d * y - (context.Height - 1)) * scale / denominator;
            for (var x = 0; x < context.Width; x++)
            {
                var real = centerX + (2d * x - (context.Width - 1)) * scale / denominator;
                var zr = real;
                var zi = imaginary;
                var iteration = 0;
                while (iteration < definition.MaxIterations && zr * zr + zi * zi <= 4d)
                {
                    var nextReal = zr * zr - zi * zi + constantReal;
                    zi = 2d * zr * zi + constantImaginary;
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

    /// <summary>
    /// 任意精度热路径使用统一二进制定点小数。位数由作品声明的十进制有效位换算而来，
    /// 每次乘法后立即缩回固定小数位，防止 BigInteger 位数在迭代中无界翻倍。
    /// </summary>
    private static ScalarField GenerateArbitrary(
        JuliaDefinition definition,
        RenderContext context,
        CancellationToken cancellationToken)
    {
        var bits = checked((int)Math.Ceiling(context.PrecisionDigits * 3.3219280948873626d) + 16);
        var centerX = FixedPoint.Parse(definition.CenterX, bits);
        var centerY = FixedPoint.Parse(definition.CenterY, bits);
        var scale = FixedPoint.Parse(definition.Scale, bits);
        var constantReal = FixedPoint.Parse(definition.ConstantReal, bits);
        var constantImaginary = FixedPoint.Parse(definition.ConstantImaginary, bits);
        var four = FixedPoint.FromInteger(4, bits);
        var denominator = checked(2 * Math.Max(1, context.Height - 1));
        var values = new float[checked(context.Width * context.Height)];
        var escaped = new bool[values.Length];

        for (var y = 0; y < context.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var imaginary = centerY.Add(scale.ScaleByRatio(2 * y - (context.Height - 1), denominator));
            for (var x = 0; x < context.Width; x++)
            {
                var real = centerX.Add(scale.ScaleByRatio(2 * x - (context.Width - 1), denominator));
                var zr = real;
                var zi = imaginary;
                var iteration = 0;
                var magnitudeSquared = FixedPoint.Zero(bits);
                while (iteration < definition.MaxIterations)
                {
                    var zrSquared = zr.Multiply(zr);
                    var ziSquared = zi.Multiply(zi);
                    magnitudeSquared = zrSquared.Add(ziSquared);
                    if (magnitudeSquared.CompareTo(four) > 0)
                    {
                        break;
                    }

                    var nextReal = zrSquared.Subtract(ziSquared).Add(constantReal);
                    zi = zr.Multiply(zi).ScaleByRatio(2, 1).Add(constantImaginary);
                    zr = nextReal;
                    iteration++;
                }

                var index = y * context.Width + x;
                if (iteration < definition.MaxIterations)
                {
                    escaped[index] = true;
                    var magnitude = Math.Max(magnitudeSquared.ToDouble(), 4.0000001d);
                    var smooth = iteration + 1d - Math.Log2(Math.Log(Math.Sqrt(magnitude)));
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

    private readonly struct FixedPoint : IComparable<FixedPoint>
    {
        private FixedPoint(BigInteger raw, int fractionalBits)
        {
            Raw = raw;
            FractionalBits = fractionalBits;
        }

        private BigInteger Raw { get; }
        private int FractionalBits { get; }

        public static FixedPoint Zero(int fractionalBits) => new(BigInteger.Zero, fractionalBits);
        public static FixedPoint FromInteger(int value, int fractionalBits) =>
            new(new BigInteger(value) << fractionalBits, fractionalBits);

        public static FixedPoint Parse(string text, int fractionalBits)
        {
            var value = ArbitraryDecimal.Parse(text);
            var decimalBudget = checked((int)Math.Ceiling(fractionalBits / 3.3219280948873626d) + 32);
            if (value.Exponent < -decimalBudget || value.Exponent > decimalBudget)
            {
                throw new InvalidDataException("数值指数超出当前渲染精度预算。");
            }

            var scaled = value.Coefficient << fractionalBits;
            if (value.Exponent >= 0)
            {
                return new FixedPoint(scaled * BigInteger.Pow(10, value.Exponent), fractionalBits);
            }

            var divisor = BigInteger.Pow(10, -value.Exponent);
            var quotient = BigInteger.DivRem(scaled, divisor, out var remainder);
            if (BigInteger.Abs(remainder) * 2 >= divisor)
            {
                quotient += value.Coefficient.Sign;
            }

            return new FixedPoint(quotient, fractionalBits);
        }

        public FixedPoint Add(FixedPoint other)
        {
            EnsureSamePrecision(other);
            return new FixedPoint(Raw + other.Raw, FractionalBits);
        }

        public FixedPoint Subtract(FixedPoint other)
        {
            EnsureSamePrecision(other);
            return new FixedPoint(Raw - other.Raw, FractionalBits);
        }

        public FixedPoint Multiply(FixedPoint other)
        {
            EnsureSamePrecision(other);
            var product = Raw * other.Raw;
            var absolute = BigInteger.Abs(product);
            var rounded = (absolute + (BigInteger.One << (FractionalBits - 1))) >> FractionalBits;
            return new FixedPoint(product.Sign < 0 ? -rounded : rounded, FractionalBits);
        }

        public FixedPoint ScaleByRatio(int numerator, int denominator)
        {
            if (denominator == 0)
            {
                throw new DivideByZeroException();
            }

            var scaled = Raw * numerator;
            var quotient = BigInteger.DivRem(scaled, denominator, out var remainder);
            if (BigInteger.Abs(remainder) * 2 >= BigInteger.Abs(denominator))
            {
                quotient += scaled.Sign * Math.Sign(denominator);
            }

            return new FixedPoint(quotient, FractionalBits);
        }

        public double ToDouble()
        {
            if (Raw.IsZero)
            {
                return 0d;
            }

            var absolute = BigInteger.Abs(Raw);
            var bitLength = absolute.GetBitLength();
            var removedBits = Math.Max(0L, bitLength - 53L);
            var leading = (double)(absolute >> checked((int)removedBits));
            var result = leading * Math.Pow(2d, removedBits - FractionalBits);
            return Raw.Sign < 0 ? -result : result;
        }

        public int CompareTo(FixedPoint other)
        {
            EnsureSamePrecision(other);
            return Raw.CompareTo(other.Raw);
        }

        private void EnsureSamePrecision(FixedPoint other)
        {
            if (FractionalBits != other.FractionalBits)
            {
                throw new InvalidOperationException("定点数精度不一致。");
            }
        }
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
