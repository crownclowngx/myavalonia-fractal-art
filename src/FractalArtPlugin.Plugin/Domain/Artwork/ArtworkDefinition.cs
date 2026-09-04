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
    RecursiveTree = 1,
    Mandelbrot = 2,
    LSystem = 3
}

public enum GeneratorFamily
{
    EscapeTime,
    LSystem
}

/// <summary>Mandelbrot 与 Julia 共用视口和精度语义，但没有用户常量，因此使用独立定义避免伪字段。</summary>
public sealed record MandelbrotDefinition(
    string CenterX,
    string CenterY,
    string Scale,
    int MaxIterations,
    bool ForceHighPrecision,
    int PrecisionDigits);

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

public sealed record LSystemRuleDefinition(char Symbol, string Replacement);

/// <summary>
/// 确定性上下文无关 L-System 配方。规则文本只描述改写，Turtle 绘制参数独立保存，
/// 因而规则展开、路径解释和描边可以分别测试与替换。
/// </summary>
public sealed record LSystemDefinition(
    string Axiom,
    IReadOnlyList<LSystemRuleDefinition> Rules,
    int Iterations,
    double TurnAngleDegrees,
    double InitialHeadingDegrees,
    double StepLength,
    double LengthDecay,
    double StrokeWidth,
    double StrokeWidthDecay)
{
    /// <summary>
    /// 规则集合会在解码时重建为新数组；领域相等性必须比较规则内容而不是集合实例，
    /// 否则内容完全相同的保存/恢复结果会被历史系统误判为一次修改。
    /// </summary>
    public bool Equals(LSystemDefinition? other) =>
        other is not null &&
        string.Equals(Axiom, other.Axiom, StringComparison.Ordinal) &&
        Rules is not null && other.Rules is not null &&
        Rules.SequenceEqual(other.Rules) &&
        Iterations == other.Iterations &&
        TurnAngleDegrees.Equals(other.TurnAngleDegrees) &&
        InitialHeadingDegrees.Equals(other.InitialHeadingDegrees) &&
        StepLength.Equals(other.StepLength) &&
        LengthDecay.Equals(other.LengthDecay) &&
        StrokeWidth.Equals(other.StrokeWidth) &&
        StrokeWidthDecay.Equals(other.StrokeWidthDecay);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Axiom, StringComparer.Ordinal);
        foreach (var rule in Rules ?? [])
        {
            hash.Add(rule);
        }

        hash.Add(Iterations);
        hash.Add(TurnAngleDegrees);
        hash.Add(InitialHeadingDegrees);
        hash.Add(StepLength);
        hash.Add(LengthDecay);
        hash.Add(StrokeWidth);
        hash.Add(StrokeWidthDecay);
        return hash.ToHashCode();
    }
}

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
    MandelbrotDefinition Mandelbrot,
    RecursiveTreeDefinition RecursiveTree,
    LSystemDefinition LSystem,
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
    MandelbrotDefinition Mandelbrot,
    RecursiveTreeDefinition RecursiveTree,
    LSystemDefinition LSystem,
    GradientDefinition Gradient,
    ArtworkPresentationDefinition Presentation,
    ArtworkExplorationDefinition Exploration,
    ArtworkGraphDefinition Graph,
    EffectChainDefinition Effects)
{
    public const int CurrentFormatVersion = 6;

    public static ArtworkDefinition CreateDefault() => new(
        CurrentFormatVersion,
        20260903,
        new CanvasDefinition(1200, 800, new RgbaColor(10, 14, 28)),
        FractalGeneratorKind.Julia,
        new JuliaDefinition("0", "0", "3.2", "-0.745", "0.113", 320, false, 96),
        new MandelbrotDefinition("-0.5", "0", "3", 320, false, 96),
        new RecursiveTreeDefinition(9, 2, 27, 0.72, 0.12, 0.28, 3.2),
        CreateDefaultLSystem(),
        new GradientDefinition(new RgbaColor(20, 31, 74), new RgbaColor(248, 167, 63), new RgbaColor(3, 5, 12)),
        new ArtworkPresentationDefinition("生成", false),
        ArtworkExplorationDefinition.CreateDefault(),
        ArtworkGraphFactory.Create(FractalGeneratorKind.Julia),
        EffectChainDefinition.Empty);

    /// <summary>从当前作品提取能够独立重放画面的最小配方。</summary>
    public VariationRecipeDefinition ToVariationRecipe() =>
        new(Seed, GeneratorKind, Julia, Mandelbrot, RecursiveTree, LSystem, Gradient);

    /// <summary>
    /// 把候选配方应用到当前作品；画布、探索收藏和 UI 呈现由当前 Document 保留，避免“采用候选”意外改掉工作区。
    /// </summary>
    public ArtworkDefinition ApplyVariationRecipe(VariationRecipeDefinition recipe) => this with
    {
        Seed = recipe.Seed,
        GeneratorKind = recipe.GeneratorKind,
        Graph = ArtworkGraphFactory.Create(recipe.GeneratorKind),
        Julia = recipe.Julia,
        Mandelbrot = recipe.Mandelbrot,
        RecursiveTree = recipe.RecursiveTree,
        LSystem = recipe.LSystem,
        Gradient = recipe.Gradient
    };

    /// <summary>
    /// 生成器类型和规范图是一个不可拆分的领域修改。所有 UI、预设和迁移都通过此方法切换，
    /// 防止作品声明 Julia、图中却保留路径节点之类的双事实错误。
    /// </summary>
    public ArtworkDefinition WithGeneratorKind(FractalGeneratorKind generatorKind) => this with
    {
        GeneratorKind = generatorKind,
        Graph = ArtworkGraphFactory.Create(generatorKind)
    };

    public GeneratorFamily GeneratorFamily => GeneratorKind is FractalGeneratorKind.Julia or FractalGeneratorKind.Mandelbrot
        ? GeneratorFamily.EscapeTime
        : GeneratorFamily.LSystem;

    private static LSystemDefinition CreateDefaultLSystem() => new(
        "X",
        [new('X', "F+[[X]-X]-F[-FX]+X"), new('F', "FF")],
        5,
        25,
        -90,
        0.018,
        1,
        3.2,
        0.82);
}
