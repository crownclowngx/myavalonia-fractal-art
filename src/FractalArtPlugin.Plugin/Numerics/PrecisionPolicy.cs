using FractalArtPlugin.Domain.Artwork;

namespace FractalArtPlugin.Numerics;

public interface IPrecisionPolicy
{
    PrecisionDescriptor Describe(JuliaDefinition definition, int viewportHeight);
}

/// <summary>
/// 保守估算当前尺度真正需要的十进制位数。策略只读取不可变输入，不关心 UI 和具体 Julia 内核，
/// 因而可以用对照测试独立校准保护位，而不修改渲染编排。
/// </summary>
internal sealed class PrecisionPolicy : IPrecisionPolicy
{
    public static PrecisionPolicy Default { get; } = new();

    public PrecisionDescriptor Describe(JuliaDefinition definition, int viewportHeight)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var scale = ArbitraryDecimal.Parse(definition.Scale);
        var values = new[]
        {
            ArbitraryDecimal.Parse(definition.CenterX),
            ArbitraryDecimal.Parse(definition.CenterY),
            scale,
            ArbitraryDecimal.Parse(definition.ConstantReal),
            ArbitraryDecimal.Parse(definition.ConstantImaginary)
        };
        var scaleDigits = Math.Max(0, -scale.AdjustedExponent);
        var parameterDigits = values.Max(value => value.SignificantDigits);
        // 迭代次数增加会放大舍入误差。这里采用简单的对数保护位并设置 16 位下限，
        // 避免建立没有测量依据的复杂误差框架。
        var iterationGuard = Math.Max(16, 12 + (int)Math.Ceiling(Math.Log10(Math.Max(16, definition.MaxIterations)) * 2d));
        var pixelGuard = (int)Math.Ceiling(Math.Log10(Math.Max(2, viewportHeight))) + 4;
        var required = Math.Max(32, Math.Max(parameterDigits, scaleDigits + Math.Max(iterationGuard, pixelGuard)));
        if (required > definition.PrecisionDigits)
        {
            throw new InsufficientPrecisionException(definition.PrecisionDigits, required);
        }

        var reason = required == definition.PrecisionDigits
            ? "当前尺度已使用全部配置精度"
            : $"由尺度 {scaleDigits} 位、参数 {parameterDigits} 位和迭代保护 {iterationGuard} 位推导";
        return new PrecisionDescriptor(
            definition.PrecisionDigits,
            required,
            Math.Min(definition.PrecisionDigits, required),
            scaleDigits,
            parameterDigits,
            iterationGuard,
            reason);
    }
}
