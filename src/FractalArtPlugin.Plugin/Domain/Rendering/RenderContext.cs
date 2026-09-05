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
    /// <summary>
    /// 当前调用允许吸引子实际生成的最大点数。它由预览质量决定并进入节点缓存键，但不写回作品，
    /// 因而最终采样配方不会因为机器或窗口状态不同而漂移。
    /// </summary>
    public int PointSampleBudget { get; init; } = int.MaxValue;

    public static RenderContext ForPreview(ArtworkDefinition artwork) => Create(artwork, RenderQuality.Draft);

    public static RenderContext ForExport(ArtworkDefinition artwork) => Create(artwork, RenderQuality.Final);

    /// <summary>
    /// 缩略图与主预览共享同一渲染质量语义，只收窄像素边界和内部并行度。这里直接从真实作品推导尺寸，
    /// 不临时改写 Canvas，因此缓存键、Alpha、合成、效果和生成器参数仍由同一条生产管线解释。
    /// </summary>
    public static RenderContext ForThumbnail(ArtworkDefinition artwork, int maximumEdge)
    {
        ArgumentNullException.ThrowIfNull(artwork);
        if (maximumEdge <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEdge), "缩略图最大边必须大于零。");
        }

        var context = Create(artwork with
        {
            Presentation = artwork.Presentation with { HighQualityPreview = false }
        }, RenderQuality.Draft);
        var ratio = Math.Min(1d, Math.Min(
            (double)maximumEdge / artwork.Canvas.Width,
            (double)maximumEdge / artwork.Canvas.Height));
        return context with
        {
            Width = Math.Max(1, (int)Math.Round(artwork.Canvas.Width * ratio)),
            Height = Math.Max(1, (int)Math.Round(artwork.Canvas.Height * ratio)),
            MaxDegreeOfParallelism = 1
        };
    }

    /// <summary>
    /// 合成帧先确定统一像素尺寸，再为每个分形层独立选择数值精度。这样深缩放层不会迫使路径层携带伪精度，
    /// 也不会错误沿用当前 UI 选中层的 Julia/Mandelbrot 策略。
    /// </summary>
    public static RenderContext ForLayer(
        ArtworkDefinition artwork,
        FractalLayerDefinition layer,
        RenderContext frame)
    {
        var selected = artwork.SelectLayer(layer.Id);
        var resolved = Create(selected, frame.Quality);
        return resolved with
        {
            Width = frame.Width,
            Height = frame.Height,
            RendererVersion = frame.RendererVersion,
            MaxDegreeOfParallelism = frame.MaxDegreeOfParallelism,
            ChunkHeight = frame.ChunkHeight,
            CancellationCheckInterval = frame.CancellationCheckInterval,
            KernelPreference = frame.KernelPreference
        };
    }

    private static RenderContext Create(ArtworkDefinition artwork, RenderQuality quality)
    {
        ArgumentNullException.ThrowIfNull(artwork);
        var isEscapeTime = artwork.GeneratorKind is FractalGeneratorKind.Julia or FractalGeneratorKind.Mandelbrot;
        var configuredDigits = artwork.GeneratorKind == FractalGeneratorKind.Mandelbrot
            ? artwork.Mandelbrot.PrecisionDigits
            : artwork.Julia.PrecisionDigits;
        var numericPrecision = artwork.GeneratorKind switch
        {
            FractalGeneratorKind.Julia => ResolveNumericPrecision(artwork.Julia),
            FractalGeneratorKind.Mandelbrot => ResolveNumericPrecision(artwork.Mandelbrot),
            _ => NumericPrecision.Double
        };
        var maximum = quality == RenderQuality.Final
            ? Math.Max(artwork.Canvas.Width, artwork.Canvas.Height)
            : isEscapeTime && numericPrecision == NumericPrecision.Arbitrary
                ? ResolveArbitraryPreviewBudget(configuredDigits, artwork.Presentation.HighQualityPreview)
                : artwork.Presentation.HighQualityPreview ? 960 : 480;
        var ratio = quality == RenderQuality.Final
            ? 1d
            : Math.Min(1d, Math.Min((double)maximum / artwork.Canvas.Width, (double)maximum / artwork.Canvas.Height));
        var width = Math.Max(1, (int)Math.Round(artwork.Canvas.Width * ratio));
        var height = Math.Max(1, (int)Math.Round(artwork.Canvas.Height * ratio));
        var descriptor = isEscapeTime && numericPrecision == NumericPrecision.Arbitrary
            ? artwork.GeneratorKind == FractalGeneratorKind.Mandelbrot
                ? PrecisionPolicy.Default.Describe(artwork.Mandelbrot, height)
                : PrecisionPolicy.Default.Describe(artwork.Julia, height)
            : new PrecisionDescriptor(
                configuredDigits,
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
            PrecisionReason = descriptor.Reason,
            PointSampleBudget = artwork.GeneratorKind == FractalGeneratorKind.StrangeAttractor
                ? quality == RenderQuality.Final
                    ? artwork.StrangeAttractor.SampleCount
                    : Math.Min(artwork.StrangeAttractor.SampleCount,
                        artwork.Presentation.HighQualityPreview ? 400_000 : 100_000)
                : int.MaxValue
        };
    }

    private static NumericPrecision ResolveNumericPrecision(JuliaDefinition julia) =>
        julia.ForceHighPrecision || ArbitraryDecimal.Parse(julia.Scale).AdjustedExponent <= -12
            ? NumericPrecision.Arbitrary
            : NumericPrecision.Double;

    private static NumericPrecision ResolveNumericPrecision(MandelbrotDefinition mandelbrot) =>
        mandelbrot.ForceHighPrecision || ArbitraryDecimal.Parse(mandelbrot.Scale).AdjustedExponent <= -12
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
