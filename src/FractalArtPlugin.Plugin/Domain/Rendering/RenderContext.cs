using FractalArtPlugin.Domain.Artwork;
using FractalArtPlugin.Numerics;

namespace FractalArtPlugin.Domain.Rendering;

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

public enum JuliaKernelPreference
{
    Automatic,
    ReferenceArbitrary,
    PerturbationExperiment
}

/// <summary>
/// 一次渲染的完整显式上下文。配置精度来自作品，有效精度来自策略；调度预算也只属于本次运行，
/// 两者均不写回作品，从而保持相同配方在不同机器上的持久化身份稳定。
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

    public int ConfiguredPrecisionDigits { get; init; } = PrecisionDigits;
    public int EffectivePrecisionDigits { get; init; } = PrecisionDigits;
    public string PrecisionReason { get; init; } = "调用方显式指定精度";
    public int MaxDegreeOfParallelism { get; init; } = ResolveDefaultDegree();
    public int ChunkHeight { get; init; } = 8;
    public int CancellationCheckInterval { get; init; } = 64;
    public JuliaKernelPreference KernelPreference { get; init; } = JuliaKernelPreference.Automatic;

    public static RenderContext ForPreview(ArtworkDefinition artwork) => Create(artwork, RenderQuality.Draft);

    public static RenderContext ForExport(ArtworkDefinition artwork) => Create(artwork, RenderQuality.Final);

    private static RenderContext Create(ArtworkDefinition artwork, RenderQuality quality)
    {
        ArgumentNullException.ThrowIfNull(artwork);
        var numericPrecision = artwork.GeneratorKind == FractalGeneratorKind.Julia
            ? ResolveNumericPrecision(artwork.Julia)
            : NumericPrecision.Double;
        var maximum = quality == RenderQuality.Final
            ? Math.Max(artwork.Canvas.Width, artwork.Canvas.Height)
            : artwork.GeneratorKind == FractalGeneratorKind.Julia && numericPrecision == NumericPrecision.Arbitrary
                ? ResolveArbitraryPreviewBudget(artwork.Julia.PrecisionDigits, artwork.Presentation.HighQualityPreview)
                : artwork.Presentation.HighQualityPreview ? 960 : 480;
        var ratio = quality == RenderQuality.Final
            ? 1d
            : Math.Min(1d, Math.Min((double)maximum / artwork.Canvas.Width, (double)maximum / artwork.Canvas.Height));
        var width = Math.Max(1, (int)Math.Round(artwork.Canvas.Width * ratio));
        var height = Math.Max(1, (int)Math.Round(artwork.Canvas.Height * ratio));
        var descriptor = artwork.GeneratorKind == FractalGeneratorKind.Julia && numericPrecision == NumericPrecision.Arbitrary
            ? PrecisionPolicy.Default.Describe(artwork.Julia, height)
            : new PrecisionDescriptor(
                artwork.Julia.PrecisionDigits,
                16,
                16,
                0,
                16,
                0,
                "当前尺度可安全使用 double 快速路径");

        return new RenderContext(
            width,
            height,
            quality,
            artwork.Seed,
            CurrentRendererVersion,
            numericPrecision,
            descriptor.EffectiveDigits)
        {
            ConfiguredPrecisionDigits = descriptor.ConfiguredDigits,
            EffectivePrecisionDigits = descriptor.EffectiveDigits,
            PrecisionReason = descriptor.Reason
        };
    }

    private static NumericPrecision ResolveNumericPrecision(JuliaDefinition julia) =>
        julia.ForceHighPrecision || ArbitraryDecimal.Parse(julia.Scale).AdjustedExponent <= -12
            ? NumericPrecision.Arbitrary
            : NumericPrecision.Double;

    private static int ResolveArbitraryPreviewBudget(int precisionDigits, bool highQuality) => precisionDigits switch
    {
        <= 128 => highQuality ? 480 : 320,
        <= 256 => highQuality ? 360 : 240,
        <= 512 => highQuality ? 240 : 160,
        _ => highQuality ? 144 : 96
    };

    private static int ResolveDefaultDegree() => Math.Max(1, Math.Min(Environment.ProcessorCount - 1, 8));
}
