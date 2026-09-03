using System.Globalization;

namespace FractalArtPlugin.Domain;

/// <summary>不可变的 RGBA 颜色值；领域层不依赖 Avalonia 的颜色类型。</summary>
public readonly record struct RgbaColor(byte Red, byte Green, byte Blue, byte Alpha = byte.MaxValue)
{
    public string ToHex() => $"#{Red:X2}{Green:X2}{Blue:X2}{Alpha:X2}";

    public static bool TryParse(string? value, out RgbaColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value) || value[0] != '#' || value.Length is not (7 or 9))
        {
            return false;
        }

        try
        {
            color = new RgbaColor(
                byte.Parse(value.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(value.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(value.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                value.Length == 9
                    ? byte.Parse(value.AsSpan(7, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                    : byte.MaxValue);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>作品画布的规范尺寸和背景；预览尺寸只是运行时投影，不回写这里。</summary>
public sealed record CanvasDefinition(int Width, int Height, RgbaColor Background);

/// <summary>
/// Julia 生成器的单一事实来源；数值以规范化十进制文本保存，避免作品在进入计算前被 double 截断。
/// </summary>
public sealed record JuliaDefinition(
    string CenterX,
    string CenterY,
    string Scale,
    string ConstantReal,
    string ConstantImaginary,
    int MaxIterations,
    bool ForceHighPrecision,
    int PrecisionDigits);

/// <summary>第一版线性渐变。内部点使用独立颜色，避免把“未逃逸”误当成渐变端点。</summary>
public sealed record GradientDefinition(RgbaColor Start, RgbaColor End, RgbaColor Interior);

/// <summary>需要随作品保存、但不参与数学求值的轻量呈现状态。</summary>
public sealed record ArtworkPresentationDefinition(string SelectedSection, bool HighQualityPreview);

/// <summary>
/// 一份完整、不可变、可序列化的分形作品配方。Document 通过替换整份值完成事务式修改，
/// 渲染任务因此可以安全捕获快照，而不必在后台读取正在变化的 ViewModel 属性。
/// </summary>
public sealed record ArtworkDefinition(
    int FormatVersion,
    long Seed,
    CanvasDefinition Canvas,
    JuliaDefinition Julia,
    GradientDefinition Gradient,
    ArtworkPresentationDefinition Presentation)
{
    public const int CurrentFormatVersion = 2;

    public static ArtworkDefinition CreateDefault() => new(
        CurrentFormatVersion,
        20260903,
        new CanvasDefinition(1200, 800, new RgbaColor(10, 14, 28)),
        new JuliaDefinition("0", "0", "3.2", "-0.745", "0.113", 320, false, 96),
        new GradientDefinition(
            new RgbaColor(20, 31, 74),
            new RgbaColor(248, 167, 63),
            new RgbaColor(3, 5, 12)),
        new ArtworkPresentationDefinition("生成", false));
}

/// <summary>集中维护作品的资源预算和数值不变量，UI 限制不能替代此领域边界。</summary>
public interface IArtworkValidator
{
    void Validate(ArtworkDefinition artwork);
}

internal sealed class ArtworkValidator : IArtworkValidator
{
    public void Validate(ArtworkDefinition artwork)
    {
        ArgumentNullException.ThrowIfNull(artwork);
        if (artwork.FormatVersion != ArtworkDefinition.CurrentFormatVersion)
        {
            throw new NotSupportedException($"不支持作品格式版本 {artwork.FormatVersion}。");
        }

        if (artwork.Canvas.Width is < 64 or > 8192 || artwork.Canvas.Height is < 64 or > 8192 ||
            (long)artwork.Canvas.Width * artwork.Canvas.Height > 64L * 1024 * 1024)
        {
            throw new InvalidDataException("画布尺寸必须位于 64–8192，且总像素不能超过 64M。");
        }

        var julia = artwork.Julia;
        if (julia.PrecisionDigits is < 32 or > 1024 ||
            !ArbitraryDecimal.TryParse(julia.CenterX, out var centerX) ||
            !ArbitraryDecimal.TryParse(julia.CenterY, out var centerY) ||
            !ArbitraryDecimal.TryParse(julia.Scale, out var scale) ||
            !ArbitraryDecimal.TryParse(julia.ConstantReal, out var constantReal) ||
            !ArbitraryDecimal.TryParse(julia.ConstantImaginary, out var constantImaginary))
        {
            throw new InvalidDataException("Julia 高精度参数格式非法，或精度不在 32–1024 位范围内。");
        }

        var minimumStoredExponent = -(julia.PrecisionDigits + 16);
        if (!IsRepresentable(centerX, julia.PrecisionDigits, minimumStoredExponent) ||
            !IsRepresentable(centerY, julia.PrecisionDigits, minimumStoredExponent) ||
            !IsRepresentable(scale, julia.PrecisionDigits, minimumStoredExponent) ||
            !IsRepresentable(constantReal, julia.PrecisionDigits, minimumStoredExponent) ||
            !IsRepresentable(constantImaginary, julia.PrecisionDigits, minimumStoredExponent) ||
            centerX.CompareTo(ArbitraryDecimal.Parse("-1000000")) < 0 ||
            centerX.CompareTo(ArbitraryDecimal.Parse("1000000")) > 0 ||
            centerY.CompareTo(ArbitraryDecimal.Parse("-1000000")) < 0 ||
            centerY.CompareTo(ArbitraryDecimal.Parse("1000000")) > 0 ||
            scale.CompareTo(ArbitraryDecimal.Zero) <= 0 ||
            scale.CompareTo(ArbitraryDecimal.Parse("10")) > 0 ||
            scale.AdjustedExponent < -(julia.PrecisionDigits - 8) ||
            constantReal.CompareTo(ArbitraryDecimal.Parse("-2")) < 0 ||
            constantReal.CompareTo(ArbitraryDecimal.Parse("2")) > 0 ||
            constantImaginary.CompareTo(ArbitraryDecimal.Parse("-2")) < 0 ||
            constantImaginary.CompareTo(ArbitraryDecimal.Parse("2")) > 0 ||
            julia.MaxIterations is < 16 or > 4096)
        {
            throw new InvalidDataException(
                "Julia 参数格式非法或超出安全预算；精度允许 32–1024 位，尺度最小指数必须为 -(精度-8)。");
        }

        if (string.IsNullOrWhiteSpace(artwork.Presentation.SelectedSection) ||
            artwork.Presentation.SelectedSection.Length > 32)
        {
            throw new InvalidDataException("呈现区域名称不能为空且不能超过 32 个字符。");
        }
    }

    private static bool IsRepresentable(ArbitraryDecimal value, int precisionDigits, int minimumExponent) =>
        value.IsZero || (value.SignificantDigits <= precisionDigits && value.Exponent >= minimumExponent);
}
