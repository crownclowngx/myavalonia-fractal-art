using System.Globalization;
using System.Numerics;

namespace FractalArtPlugin.Domain;

/// <summary>
/// 用“任意长度整数 × 10 的整数次幂”保存用户可见的精确十进制数。
/// </summary>
/// <remarks>
/// 该类型只承担作品参数、视口变换和持久化所需的十进制运算，不试图成为通用数学库。
/// Julia 的逐像素热路径会把它转换为二进制定点数，避免每次迭代反复对齐十进制指数。
/// </remarks>
public readonly struct ArbitraryDecimal : IComparable<ArbitraryDecimal>, IEquatable<ArbitraryDecimal>
{
    private const int MaximumInputDigits = 4096;
    private const int MaximumAbsoluteExponent = 100_000;

    private ArbitraryDecimal(BigInteger coefficient, int exponent)
    {
        if (coefficient.IsZero)
        {
            Coefficient = BigInteger.Zero;
            Exponent = 0;
            return;
        }

        while (BigInteger.Remainder(coefficient, 10).IsZero)
        {
            coefficient /= 10;
            exponent = checked(exponent + 1);
        }

        Coefficient = coefficient;
        Exponent = exponent;
    }

    public BigInteger Coefficient { get; }
    public int Exponent { get; }
    public bool IsZero => Coefficient.IsZero;
    public int SignificantDigits => DigitCount(BigInteger.Abs(Coefficient));
    public int AdjustedExponent => IsZero ? 0 : checked(Exponent + DigitCount(BigInteger.Abs(Coefficient)) - 1);

    public static ArbitraryDecimal Zero => new(BigInteger.Zero, 0);
    public static ArbitraryDecimal One => new(BigInteger.One, 0);

    public static ArbitraryDecimal Parse(string text)
    {
        if (!TryParse(text, out var value))
        {
            throw new FormatException($"无法把“{text}”解析为高精度十进制数。");
        }

        return value;
    }

    public static bool TryParse(string? text, out ArbitraryDecimal value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var source = text.Trim();
        var exponentIndex = source.IndexOfAny('e', 'E');
        var significand = exponentIndex < 0 ? source : source[..exponentIndex];
        var exponentText = exponentIndex < 0 ? null : source[(exponentIndex + 1)..];
        if (exponentIndex >= 0 && (string.IsNullOrWhiteSpace(exponentText) ||
            !int.TryParse(exponentText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)))
        {
            return false;
        }

        var decimalExponent = exponentText is null
            ? 0
            : int.Parse(exponentText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        if (Math.Abs((long)decimalExponent) > MaximumAbsoluteExponent)
        {
            return false;
        }

        var negative = significand.StartsWith('-');
        if (negative || significand.StartsWith('+'))
        {
            significand = significand[1..];
        }

        var separator = significand.IndexOf('.');
        if (separator != significand.LastIndexOf('.') || significand.Length == 0)
        {
            return false;
        }

        var fractionalDigits = separator < 0 ? 0 : significand.Length - separator - 1;
        var digits = separator < 0 ? significand : string.Concat(significand.AsSpan(0, separator), significand.AsSpan(separator + 1));
        if (digits.Length == 0 || digits.Length > MaximumInputDigits || digits.Any(character => character is < '0' or > '9'))
        {
            return false;
        }

        var coefficient = BigInteger.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        if (negative)
        {
            coefficient = -coefficient;
        }

        try
        {
            value = new ArbitraryDecimal(coefficient, checked(decimalExponent - fractionalDigits));
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    public ArbitraryDecimal Add(ArbitraryDecimal other, int significantDigits)
    {
        ValidatePrecision(significantDigits);
        var commonExponent = Math.Min(Exponent, other.Exponent);
        var left = Coefficient * PowerOfTen(Exponent - commonExponent);
        var right = other.Coefficient * PowerOfTen(other.Exponent - commonExponent);
        return new ArbitraryDecimal(left + right, commonExponent).Round(significantDigits);
    }

    public ArbitraryDecimal Subtract(ArbitraryDecimal other, int significantDigits) =>
        Add(other.Negate(), significantDigits);

    public ArbitraryDecimal Multiply(ArbitraryDecimal other, int significantDigits)
    {
        ValidatePrecision(significantDigits);
        return new ArbitraryDecimal(
            Coefficient * other.Coefficient,
            checked(Exponent + other.Exponent)).Round(significantDigits);
    }

    public ArbitraryDecimal Divide(int divisor, int significantDigits)
    {
        if (divisor == 0)
        {
            throw new DivideByZeroException();
        }

        ValidatePrecision(significantDigits);
        if (IsZero)
        {
            return Zero;
        }

        var extraDigits = Math.Max(0, significantDigits + DigitCount(BigInteger.Abs(divisor)) -
            DigitCount(BigInteger.Abs(Coefficient)) + 2);
        var scale = PowerOfTen(extraDigits);
        var quotient = BigInteger.DivRem(Coefficient * scale, divisor, out var remainder);
        if (BigInteger.Abs(remainder) * 2 >= BigInteger.Abs(divisor))
        {
            quotient += Coefficient.Sign * Math.Sign(divisor);
        }

        return new ArbitraryDecimal(quotient, checked(Exponent - extraDigits)).Round(significantDigits);
    }

    public ArbitraryDecimal Negate() => new(-Coefficient, Exponent);

    public ArbitraryDecimal Round(int significantDigits)
    {
        ValidatePrecision(significantDigits);
        var digits = DigitCount(BigInteger.Abs(Coefficient));
        if (IsZero || digits <= significantDigits)
        {
            return this;
        }

        var removedDigits = digits - significantDigits;
        var divisor = PowerOfTen(removedDigits);
        var quotient = BigInteger.DivRem(Coefficient, divisor, out var remainder);
        if (BigInteger.Abs(remainder) * 2 >= divisor)
        {
            quotient += Coefficient.Sign;
        }

        return new ArbitraryDecimal(quotient, checked(Exponent + removedDigits));
    }

    public double ToDouble() => double.Parse(ToString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    public int CompareTo(ArbitraryDecimal other)
    {
        if (Coefficient.Sign != other.Coefficient.Sign)
        {
            return Coefficient.Sign.CompareTo(other.Coefficient.Sign);
        }

        if (IsZero)
        {
            return 0;
        }

        var commonExponent = Math.Min(Exponent, other.Exponent);
        return (Coefficient * PowerOfTen(Exponent - commonExponent)).CompareTo(
            other.Coefficient * PowerOfTen(other.Exponent - commonExponent));
    }

    public override string ToString()
    {
        if (IsZero)
        {
            return "0";
        }

        var negative = Coefficient.Sign < 0;
        var digits = BigInteger.Abs(Coefficient).ToString(CultureInfo.InvariantCulture);
        var adjustedExponent = checked(Exponent + digits.Length - 1);
        var fraction = digits.Length == 1 ? string.Empty : $".{digits[1..]}";
        return $"{(negative ? "-" : string.Empty)}{digits[0]}{fraction}e{adjustedExponent}";
    }

    public bool Equals(ArbitraryDecimal other) => Coefficient == other.Coefficient && Exponent == other.Exponent;
    public override bool Equals(object? obj) => obj is ArbitraryDecimal other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Coefficient, Exponent);

    private static BigInteger PowerOfTen(int exponent)
    {
        if (exponent < 0 || exponent > MaximumAbsoluteExponent)
        {
            throw new ArgumentOutOfRangeException(nameof(exponent), "十进制指数差超出安全预算。");
        }

        return BigInteger.Pow(10, exponent);
    }

    private static int DigitCount(BigInteger value) =>
        value.IsZero ? 1 : value.ToString(CultureInfo.InvariantCulture).Length;

    private static void ValidatePrecision(int significantDigits)
    {
        if (significantDigits is < 1 or > MaximumInputDigits)
        {
            throw new ArgumentOutOfRangeException(nameof(significantDigits));
        }
    }
}

/// <summary>集中实现鼠标平移和以指针为锚点的缩放，保证 UI 不直接操作高精度字符串。</summary>
public static class HighPrecisionViewport
{
    public static JuliaDefinition Pan(
        JuliaDefinition viewport,
        double deltaX,
        double deltaY,
        double viewportHeight)
    {
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY) || !double.IsFinite(viewportHeight) || viewportHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(viewportHeight), "视口尺寸和拖动距离必须是有限数值。");
        }

        var digits = viewport.PrecisionDigits;
        var scale = ArbitraryDecimal.Parse(viewport.Scale);
        var xOffset = scale.Multiply(FromDouble(-deltaX), digits).Divide(Math.Max(1, (int)Math.Round(viewportHeight)), digits);
        var yOffset = scale.Multiply(FromDouble(-deltaY), digits).Divide(Math.Max(1, (int)Math.Round(viewportHeight)), digits);
        return viewport with
        {
            CenterX = ArbitraryDecimal.Parse(viewport.CenterX).Add(xOffset, digits).ToString(),
            CenterY = ArbitraryDecimal.Parse(viewport.CenterY).Add(yOffset, digits).ToString()
        };
    }

    public static JuliaDefinition ZoomAt(
        JuliaDefinition viewport,
        double pointerX,
        double pointerY,
        double viewportWidth,
        double viewportHeight,
        double wheelDelta)
    {
        if (!double.IsFinite(pointerX) || !double.IsFinite(pointerY) ||
            !double.IsFinite(viewportWidth) || !double.IsFinite(viewportHeight) ||
            viewportWidth <= 0 || viewportHeight <= 0 || wheelDelta == 0)
        {
            return viewport;
        }

        var digits = viewport.PrecisionDigits;
        var steps = Math.Clamp((int)Math.Ceiling(Math.Abs(wheelDelta)), 1, 8);
        var factor = ArbitraryDecimal.One;
        var stepFactor = ArbitraryDecimal.Parse(wheelDelta > 0 ? "0.8" : "1.25");
        for (var step = 0; step < steps; step++)
        {
            factor = factor.Multiply(stepFactor, digits);
        }

        var scale = ArbitraryDecimal.Parse(viewport.Scale);
        var height = Math.Max(1, (int)Math.Round(viewportHeight));
        var xFromCenter = FromDouble(pointerX - viewportWidth / 2d);
        var yFromCenter = FromDouble(pointerY - viewportHeight / 2d);
        var xOffset = scale.Multiply(xFromCenter, digits).Divide(height, digits);
        var yOffset = scale.Multiply(yFromCenter, digits).Divide(height, digits);
        var anchorShiftFactor = ArbitraryDecimal.One.Subtract(factor, digits);

        return viewport with
        {
            CenterX = ArbitraryDecimal.Parse(viewport.CenterX)
                .Add(xOffset.Multiply(anchorShiftFactor, digits), digits).ToString(),
            CenterY = ArbitraryDecimal.Parse(viewport.CenterY)
                .Add(yOffset.Multiply(anchorShiftFactor, digits), digits).ToString(),
            Scale = scale.Multiply(factor, digits).ToString()
        };
    }

    private static ArbitraryDecimal FromDouble(double value) =>
        ArbitraryDecimal.Parse(value.ToString("R", CultureInfo.InvariantCulture));
}
