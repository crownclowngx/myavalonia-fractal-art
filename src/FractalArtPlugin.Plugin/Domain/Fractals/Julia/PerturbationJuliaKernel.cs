using System.Numerics;
using FractalArtPlugin.Domain.Artwork;
using FractalArtPlugin.Domain.Rendering;
using FractalArtPlugin.Numerics;

namespace FractalArtPlugin.Domain.Fractals.Julia;

/// <summary>
/// P07 的显式实验内核。中心参考轨道用任意精度计算，像素只用 double 推进相对量；
/// 下溢、非有限值或阈值附近样本都会逐像素回退权威定点内核。当前不参与 Automatic 选择。
/// </summary>
internal sealed class PerturbationJuliaKernel : IJuliaKernel
{
    public string Name => "perturbation-experiment";

    public bool CanHandle(RenderContext context) =>
        context.NumericPrecision == NumericPrecision.Arbitrary &&
        (context.KernelPreference == JuliaKernelPreference.PerturbationExperiment ||
         (context.KernelPreference == JuliaKernelPreference.Automatic &&
          context.Quality == RenderQuality.Draft &&
          context.EffectivePrecisionDigits >= 64));

    public ScalarField Generate(JuliaDefinition definition, RenderContext context, CancellationToken cancellationToken)
    {
        var fixedPoint = BinaryFixedPoint.ForDecimalDigits(context.EffectivePrecisionDigits);
        var frame = ArbitraryJuliaKernel.FrameCoordinates.Create(definition, context, fixedPoint);
        var centerReal = fixedPoint.Parse(definition.CenterX);
        var centerImaginary = fixedPoint.Parse(definition.CenterY);
        var reference = BuildReferenceOrbit(
            fixedPoint,
            centerReal,
            centerImaginary,
            frame.ConstantReal,
            frame.ConstantImaginary,
            definition.MaxIterations,
            cancellationToken);
        var values = new float[checked(context.Width * context.Height)];
        var escaped = new bool[values.Length];
        var glitches = 0;

        Parallel.For(0, context.Height, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = context.MaxDegreeOfParallelism
        }, y =>
        {
            var imaginary = frame.Top + y * frame.PixelStep;
            var real = frame.Left;
            for (var x = 0; x < context.Width; x++, real += frame.PixelStep)
            {
                if (x % context.CancellationCheckInterval == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var deltaRealRaw = real - centerReal;
                var deltaImaginaryRaw = imaginary - centerImaginary;
                // ScaledNumber 把尾数与二进制指数分开保存。普通 double 在 1e-1000 会直接变成零，
                // 分离指数后仍可推进扰动量；只有真正溢出、非有限或阈值不确定时才回退完整内核。
                var deltaReal = ScaledNumber.FromRaw(deltaRealRaw, fixedPoint.FractionalBits);
                var deltaImaginary = ScaledNumber.FromRaw(deltaImaginaryRaw, fixedPoint.FractionalBits);
                var iteration = 0;
                var magnitudeSquared = 0d;
                var glitch = false;
                while (!glitch && iteration < definition.MaxIterations)
                {
                    var referencePoint = reference[iteration];
                    if (!referencePoint.IsValid)
                    {
                        // 参考点已经逃逸后继续平方会让 BigInteger 位数指数膨胀；此时少数仍未逃逸的像素
                        // 直接回退完整内核，比维护没有意义的超大参考轨道更安全。
                        glitch = true;
                        break;
                    }

                    var zr = referencePoint.Real + deltaReal.ToDouble();
                    var zi = referencePoint.Imaginary + deltaImaginary.ToDouble();
                    magnitudeSquared = zr * zr + zi * zi;
                    if (!double.IsFinite(magnitudeSquared))
                    {
                        glitch = true;
                        break;
                    }

                    if (magnitudeSquared > 4d)
                    {
                        // 距阈值过近时 double 误差可能改变分类，交给权威内核复核。
                        glitch = magnitudeSquared - 4d <= Math.Max(1e-12, Math.Abs(magnitudeSquared) * 1e-13);
                        break;
                    }

                    var nextDeltaReal = deltaReal.Multiply(2d * referencePoint.Real)
                        .Subtract(deltaImaginary.Multiply(2d * referencePoint.Imaginary))
                        .Add(deltaReal.Multiply(deltaReal))
                        .Subtract(deltaImaginary.Multiply(deltaImaginary));
                    var nextDeltaImaginary = deltaImaginary.Multiply(2d * referencePoint.Real)
                        .Add(deltaReal.Multiply(2d * referencePoint.Imaginary))
                        .Add(deltaReal.Multiply(deltaImaginary).Multiply(2d));
                    deltaReal = nextDeltaReal;
                    deltaImaginary = nextDeltaImaginary;
                    glitch = !deltaReal.IsFinite || !deltaImaginary.IsFinite;
                    iteration++;
                }

                ArbitraryJuliaKernel.PixelSample sample;
                if (glitch)
                {
                    sample = ArbitraryJuliaKernel.ComputePixel(
                        fixedPoint,
                        real,
                        imaginary,
                        frame.ConstantReal,
                        frame.ConstantImaginary,
                        definition.MaxIterations,
                        context.CancellationCheckInterval,
                        cancellationToken);
                    Interlocked.Increment(ref glitches);
                }
                else
                {
                    sample = new ArbitraryJuliaKernel.PixelSample(iteration, magnitudeSquared, false);
                }

                sample.Write(values, escaped, y * context.Width + x, definition.MaxIterations);
            }
        });

        return ScalarField.FromOwned(context.Width, context.Height, values, escaped,
            new RenderDiagnostics(
                Name,
                context.ConfiguredPrecisionDigits,
                context.EffectivePrecisionDigits,
                context.MaxDegreeOfParallelism,
                glitches,
                glitches));
    }

    private static ReferencePoint[] BuildReferenceOrbit(
        BinaryFixedPoint fixedPoint,
        BigInteger initialReal,
        BigInteger initialImaginary,
        BigInteger constantReal,
        BigInteger constantImaginary,
        int maximumIterations,
        CancellationToken cancellationToken)
    {
        var orbit = new ReferencePoint[maximumIterations + 1];
        var zr = initialReal;
        var zi = initialImaginary;
        for (var iteration = 0; iteration <= maximumIterations; iteration++)
        {
            if ((iteration & 31) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            orbit[iteration] = new ReferencePoint(fixedPoint.ToDouble(zr), fixedPoint.ToDouble(zi), true);
            var zrSquared = fixedPoint.Multiply(zr, zr);
            var ziSquared = fixedPoint.Multiply(zi, zi);
            if (zrSquared + ziSquared > fixedPoint.Four)
            {
                break;
            }

            zi = (fixedPoint.Multiply(zr, zi) << 1) + constantImaginary;
            zr = zrSquared - ziSquared + constantReal;
        }

        return orbit;
    }

    private readonly record struct ReferencePoint(double Real, double Imaginary, bool IsValid);

    /// <summary>
    /// 只服务扰动量的轻量扩展浮点：value = mantissa × 2^exponent。它没有通用数学类型的野心，
    /// 仅实现递推式所需的加、减、乘，并把相差超过 60 位的较小加数安全忽略到 double 尾数之外。
    /// </summary>
    private readonly record struct ScaledNumber(double Mantissa, int Exponent)
    {
        public bool IsFinite => double.IsFinite(Mantissa);

        public static ScaledNumber FromRaw(BigInteger raw, int fractionalBits)
        {
            if (raw.IsZero)
            {
                return default;
            }

            var absolute = BigInteger.Abs(raw);
            var removedBits = Math.Max(0L, absolute.GetBitLength() - 53L);
            var leading = (double)(absolute >> checked((int)removedBits));
            return Normalize(raw.Sign < 0 ? -leading : leading, checked((int)(removedBits - fractionalBits)));
        }

        public ScaledNumber Add(ScaledNumber other)
        {
            if (Mantissa == 0d)
            {
                return other;
            }

            if (other.Mantissa == 0d)
            {
                return this;
            }

            if (Exponent >= other.Exponent)
            {
                var difference = Exponent - other.Exponent;
                return difference > 60
                    ? this
                    : Normalize(Mantissa + Math.ScaleB(other.Mantissa, -difference), Exponent);
            }

            return other.Add(this);
        }

        public ScaledNumber Subtract(ScaledNumber other) => Add(new ScaledNumber(-other.Mantissa, other.Exponent));

        public ScaledNumber Multiply(double factor) => Normalize(Mantissa * factor, Exponent);

        public ScaledNumber Multiply(ScaledNumber other) =>
            Normalize(Mantissa * other.Mantissa, checked(Exponent + other.Exponent));

        public double ToDouble() => Math.ScaleB(Mantissa, Exponent);

        private static ScaledNumber Normalize(double mantissa, int exponent)
        {
            if (mantissa == 0d || !double.IsFinite(mantissa))
            {
                return new ScaledNumber(mantissa, exponent);
            }

            var shift = Math.ILogB(Math.Abs(mantissa));
            return new ScaledNumber(Math.ScaleB(mantissa, -shift), checked(exponent + shift));
        }
    }
}
