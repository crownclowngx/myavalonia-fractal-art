using System.Numerics;
using FractalArtPlugin.Numerics;

namespace FractalArtPlugin.Domain.Fractals;

/// <summary>逃逸时间单步轨迹点。Iteration 表示该点是 z 的第几次状态，0 即初始值。</summary>
internal readonly record struct EscapeOrbitPoint(int Iteration, double Real, double Imaginary);

/// <summary>
/// Julia 与 Mandelbrot 共同使用的单点递推核心。生产内核传入空收集器时不会为轨迹分配内存；数学透镜
/// 传入列表时才记录 z₀…zₙ。这样演示和真实像素共享完全相同的乘法顺序、逃逸阈值与取消检查。
/// </summary>
internal static class EscapeOrbitMath
{
    public static EscapeOrbitSample ComputeDouble(
        double initialReal,
        double initialImaginary,
        double constantReal,
        double constantImaginary,
        int maximumIterations,
        int cancellationCheckInterval,
        CancellationToken cancellationToken,
        ICollection<EscapeOrbitPoint>? trace = null)
    {
        var real = initialReal;
        var imaginary = initialImaginary;
        var iteration = 0;
        trace?.Add(new EscapeOrbitPoint(0, real, imaginary));
        while (iteration < maximumIterations && real * real + imaginary * imaginary <= 4d)
        {
            if (iteration % cancellationCheckInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var nextReal = real * real - imaginary * imaginary + constantReal;
            imaginary = 2d * real * imaginary + constantImaginary;
            real = nextReal;
            iteration++;
            trace?.Add(new EscapeOrbitPoint(iteration, real, imaginary));
        }

        return new EscapeOrbitSample(iteration, real * real + imaginary * imaginary, false);
    }

    public static EscapeOrbitSample ComputeFixed(
        BinaryFixedPoint fixedPoint,
        BigInteger initialReal,
        BigInteger initialImaginary,
        BigInteger constantReal,
        BigInteger constantImaginary,
        int maximumIterations,
        int cancellationCheckInterval,
        CancellationToken cancellationToken,
        ICollection<EscapeOrbitPoint>? trace = null)
    {
        var real = initialReal;
        var imaginary = initialImaginary;
        var iteration = 0;
        var magnitudeSquared = BigInteger.Zero;
        trace?.Add(new EscapeOrbitPoint(0, fixedPoint.ToDouble(real), fixedPoint.ToDouble(imaginary)));
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
            trace?.Add(new EscapeOrbitPoint(iteration, fixedPoint.ToDouble(real), fixedPoint.ToDouble(imaginary)));
        }

        var guardShift = Math.Min(fixedPoint.FractionalBits, 48);
        var guard = BigInteger.One << Math.Max(0, fixedPoint.FractionalBits - guardShift);
        var nearBoundary = BigInteger.Abs(magnitudeSquared - fixedPoint.Four) <= guard;
        return new EscapeOrbitSample(iteration, fixedPoint.ToDouble(magnitudeSquared), nearBoundary);
    }
}

internal readonly record struct EscapeOrbitSample(
    int Iteration,
    double MagnitudeSquared,
    bool NearEscapeBoundary)
{
    public void Write(float[] values, bool[] escaped, int index, int maximumIterations) =>
        Julia.JuliaScalar.Write(values, escaped, index, Iteration, maximumIterations, MagnitudeSquared);

    public (float Value, bool Escaped) ToScalar(int maximumIterations)
    {
        var values = new float[1];
        var escaped = new bool[1];
        Write(values, escaped, 0, maximumIterations);
        return (values[0], escaped[0]);
    }
}
