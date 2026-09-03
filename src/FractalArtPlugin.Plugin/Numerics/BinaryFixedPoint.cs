using System.Numerics;

namespace FractalArtPlugin.Numerics;

/// <summary>
/// Julia 内核私有的二进制定点上下文。调用者在进入热循环前保证所有 raw 值使用同一小数位，
/// 因此这里不为每次加减携带精度字段或重复校验；它不是公共数学类型。
/// </summary>
internal sealed class BinaryFixedPoint(int fractionalBits)
{
    private readonly BigInteger _rounding = BigInteger.One << (fractionalBits - 1);

    public int FractionalBits { get; } = fractionalBits;
    public BigInteger Four { get; } = new BigInteger(4) << fractionalBits;

    public static BinaryFixedPoint ForDecimalDigits(int decimalDigits) =>
        new(checked((int)Math.Ceiling(decimalDigits * 3.3219280948873626d) + 16));

    public BigInteger Parse(string text)
    {
        var value = ArbitraryDecimal.Parse(text);
        var decimalBudget = checked((int)Math.Ceiling(FractionalBits / 3.3219280948873626d) + 32);
        if (value.Exponent < -decimalBudget || value.Exponent > decimalBudget)
        {
            throw new InvalidDataException("数值指数超出当前渲染精度预算。");
        }

        var scaled = value.Coefficient << FractionalBits;
        if (value.Exponent >= 0)
        {
            return scaled * BigInteger.Pow(10, value.Exponent);
        }

        return DivideRounded(scaled, BigInteger.Pow(10, -value.Exponent));
    }

    public BigInteger Multiply(BigInteger left, BigInteger right)
    {
        var product = left * right;
        var rounded = (BigInteger.Abs(product) + _rounding) >> FractionalBits;
        return product.Sign < 0 ? -rounded : rounded;
    }

    public static BigInteger DivideRounded(BigInteger value, BigInteger divisor)
    {
        if (divisor.IsZero)
        {
            throw new DivideByZeroException();
        }

        var quotient = BigInteger.DivRem(value, divisor, out var remainder);
        if (BigInteger.Abs(remainder) * 2 >= BigInteger.Abs(divisor))
        {
            quotient += value.Sign * divisor.Sign;
        }

        return quotient;
    }

    public double ToDouble(BigInteger raw)
    {
        if (raw.IsZero)
        {
            return 0d;
        }

        var absolute = BigInteger.Abs(raw);
        var removedBits = Math.Max(0L, absolute.GetBitLength() - 53L);
        var leading = (double)(absolute >> checked((int)removedBits));
        var result = leading * Math.Pow(2d, removedBits - FractionalBits);
        return raw.Sign < 0 ? -result : result;
    }
}
