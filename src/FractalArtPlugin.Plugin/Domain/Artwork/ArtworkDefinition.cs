using System.Globalization;
using FractalArtPlugin.Numerics;

namespace FractalArtPlugin.Domain.Artwork;

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

public sealed record CanvasDefinition(int Width, int Height, RgbaColor Background);

/// <summary>Julia 配方只保存用户配置上限；有效精度和线程预算属于单次渲染上下文。</summary>
public sealed record JuliaDefinition(
    string CenterX,
    string CenterY,
    string Scale,
    string ConstantReal,
    string ConstantImaginary,
    int MaxIterations,
    bool ForceHighPrecision,
    int PrecisionDigits);

public sealed record GradientDefinition(RgbaColor Start, RgbaColor End, RgbaColor Interior);
public sealed record ArtworkPresentationDefinition(string SelectedSection, bool HighQualityPreview);

/// <summary>
/// 完整且不可变的作品配方。运行时诊断没有混入此对象，所以 v2 往返不会受机器核心数或策略选择影响。
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
        new GradientDefinition(new RgbaColor(20, 31, 74), new RgbaColor(248, 167, 63), new RgbaColor(3, 5, 12)),
        new ArtworkPresentationDefinition("生成", false));
}
