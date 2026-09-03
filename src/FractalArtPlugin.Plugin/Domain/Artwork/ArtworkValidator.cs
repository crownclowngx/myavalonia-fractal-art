using FractalArtPlugin.Numerics;

namespace FractalArtPlugin.Domain.Artwork;

public interface IArtworkValidator
{
    void Validate(ArtworkDefinition artwork);
}

/// <summary>集中维护作品资源预算；UI 限制、渲染策略与持久化均不能绕过这一领域边界。</summary>
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
