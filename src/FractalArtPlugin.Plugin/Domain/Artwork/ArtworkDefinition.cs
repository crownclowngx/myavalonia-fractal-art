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

/// <summary>
/// 作品当前使用的生成器类型。枚举值会进入持久化文件，因此只能追加，不能重排或复用已有数值。
/// </summary>
public enum FractalGeneratorKind
{
    Julia = 0,
    RecursiveTree = 1
}

/// <summary>
/// 递归树的矢量配方。坐标和长度采用归一化画布语义，生成阶段只产生线段，不接触像素缓冲区。
/// 深度、分叉数和线段总量由统一验证器共同约束，避免参数组合造成指数级资源失控。
/// </summary>
public sealed record RecursiveTreeDefinition(
    int Depth,
    int Branches,
    double BranchAngleDegrees,
    double LengthDecay,
    double Randomness,
    double TrunkLength,
    double StrokeWidth);

public sealed record GradientDefinition(RgbaColor Start, RgbaColor End, RgbaColor Interior);
public sealed record ArtworkPresentationDefinition(string SelectedSection, bool HighQualityPreview);

/// <summary>
/// 可独立重放的渲染配方。候选和收藏只保存影响画面的真实参数，不复制画布、呈现状态或探索状态，
/// 从而避免在作品快照中形成递归对象图。
/// </summary>
public sealed record VariationRecipeDefinition(
    long Seed,
    FractalGeneratorKind GeneratorKind,
    JuliaDefinition Julia,
    RecursiveTreeDefinition RecursiveTree,
    GradientDefinition Gradient);

public sealed record VariationCandidateDefinition(
    string Id,
    int Number,
    VariationRecipeDefinition Recipe);

public sealed record FavoriteVariationDefinition(
    string Id,
    string Name,
    VariationRecipeDefinition Recipe);

[Flags]
public enum VariationLockGroups
{
    None = 0,
    Seed = 1,
    Composition = 2,
    Shape = 4,
    Color = 8
}

public enum VariationMode
{
    All,
    ShapeOnly,
    TextureOnly
}

/// <summary>
/// 探索状态属于作品配方：强度、锁定、轮次及候选决定下一轮随机序列，候选和收藏则保证保存后仍可恢复。
/// 缩略图是可重新计算的运行时缓存，刻意不进入这里。
/// </summary>
public sealed record ArtworkExplorationDefinition(
    double MutationStrength,
    VariationLockGroups Locks,
    VariationMode Mode,
    int Generation,
    IReadOnlyList<VariationCandidateDefinition> Candidates,
    IReadOnlyList<FavoriteVariationDefinition> Favorites)
{
    public static ArtworkExplorationDefinition CreateDefault() =>
        new(0.35, VariationLockGroups.None, VariationMode.All, 0, [], []);
}

/// <summary>
/// 完整且不可变的作品配方。运行时诊断没有混入此对象，所以快照往返不会受机器核心数或策略选择影响。
/// </summary>
public sealed record ArtworkDefinition(
    int FormatVersion,
    long Seed,
    CanvasDefinition Canvas,
    FractalGeneratorKind GeneratorKind,
    JuliaDefinition Julia,
    RecursiveTreeDefinition RecursiveTree,
    GradientDefinition Gradient,
    ArtworkPresentationDefinition Presentation,
    ArtworkExplorationDefinition Exploration)
{
    public const int CurrentFormatVersion = 4;

    public static ArtworkDefinition CreateDefault() => new(
        CurrentFormatVersion,
        20260903,
        new CanvasDefinition(1200, 800, new RgbaColor(10, 14, 28)),
        FractalGeneratorKind.Julia,
        new JuliaDefinition("0", "0", "3.2", "-0.745", "0.113", 320, false, 96),
        new RecursiveTreeDefinition(9, 2, 27, 0.72, 0.12, 0.28, 3.2),
        new GradientDefinition(new RgbaColor(20, 31, 74), new RgbaColor(248, 167, 63), new RgbaColor(3, 5, 12)),
        new ArtworkPresentationDefinition("生成", false),
        ArtworkExplorationDefinition.CreateDefault());

    /// <summary>从当前作品提取能够独立重放画面的最小配方。</summary>
    public VariationRecipeDefinition ToVariationRecipe() =>
        new(Seed, GeneratorKind, Julia, RecursiveTree, Gradient);

    /// <summary>
    /// 把候选配方应用到当前作品；画布、探索收藏和 UI 呈现由当前 Document 保留，避免“采用候选”意外改掉工作区。
    /// </summary>
    public ArtworkDefinition ApplyVariationRecipe(VariationRecipeDefinition recipe) => this with
    {
        Seed = recipe.Seed,
        GeneratorKind = recipe.GeneratorKind,
        Julia = recipe.Julia,
        RecursiveTree = recipe.RecursiveTree,
        Gradient = recipe.Gradient
    };
}
