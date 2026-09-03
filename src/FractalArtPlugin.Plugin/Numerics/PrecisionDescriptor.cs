namespace FractalArtPlugin.Numerics;

/// <summary>一次渲染的精度决策；该运行时诊断不会进入作品持久化。</summary>
public sealed record PrecisionDescriptor(
    int ConfiguredDigits,
    int RequiredDigits,
    int EffectiveDigits,
    int ScaleDigits,
    int ParameterDigits,
    int IterationGuardDigits,
    string Reason);

/// <summary>明确表示用户配置上限不足，而不是静默降低当前像素所需精度。</summary>
public sealed class InsufficientPrecisionException(int configuredDigits, int requiredDigits)
    : InvalidOperationException($"配置精度不足：当前配置 {configuredDigits} 位，本次渲染至少需要 {requiredDigits} 位。")
{
    public int ConfiguredDigits { get; } = configuredDigits;
    public int RequiredDigits { get; } = requiredDigits;
}
