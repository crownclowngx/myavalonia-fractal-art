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
    LSystem = 3,
    StrangeAttractor = 4
}

public enum GeneratorFamily
{
    EscapeTime,
    LSystem,
    Attractor
}

/// <summary>
/// 奇异吸引子公式身份会进入作品文件，只能追加不能重排。两个公式共享点云、密度与图层基础设施，
/// 公式策略自身只负责一次状态迭代，避免复制整条渲染管线。
/// </summary>
public enum AttractorFormula
{
    Clifford = 0,
    DeJong = 1
}

/// <summary>
/// 奇异吸引子的完整可重放配方。SampleCount 是最终质量上限；预览的实际点数由渲染上下文控制，
/// 不会反向修改作品。局部发光属于当前分形层，不会改变整幅作品的 Master Bloom。
/// </summary>
public sealed record StrangeAttractorDefinition(
    AttractorFormula Formula,
    double A,
    double B,
    double C,
    double D,
    int BurnInIterations,
    int SampleCount,
    double Exposure,
    double Gamma,
    bool GlowEnabled,
    double GlowSigma,
    double GlowStrength);

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
public sealed record ArtworkPresentationDefinition(
    string SelectedSection,
    bool HighQualityPreview,
    string SelectedLayerId = "layer-1");

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
    StrangeAttractorDefinition StrangeAttractor,
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
public enum LayerBlendMode
{
    Normal = 0,
    Multiply = 1,
    Screen = 2,
    Add = 3,
    Overlay = 4
}

/// <summary>
/// 图层变换使用相对画布的百分比，因而修改画布尺寸后仍保持构图比例。正角度在屏幕坐标系中顺时针旋转；
/// 锚点先平移到原点，再应用统一缩放和旋转，最后回到锚点并叠加位置偏移。
/// </summary>
public sealed record LayerTransformDefinition(
    double PositionXPercent,
    double PositionYPercent,
    double ScalePercent,
    double RotationDegrees,
    double AnchorXPercent,
    double AnchorYPercent)
{
    public static LayerTransformDefinition Identity { get; } = new(0, 0, 100, 0, 50, 50);
}

/// <summary>
/// 遮罩只引用逃逸时间分形的原始标量场，不引用着色后的像素。这样更换遮罩源调色板不会让目标层失效，
/// 也避免把“亮度遮罩”与“数学数据遮罩”混为一谈。
/// </summary>
public sealed record ScalarMaskDefinition(string SourceLayerId, double Threshold, double Softness, bool IsInverted);

public abstract record ArtworkLayerDefinition(
    string Id,
    string Name,
    bool IsVisible,
    double Opacity,
    LayerBlendMode BlendMode,
    LayerTransformDefinition Transform,
    ScalarMaskDefinition? Mask);

/// <summary>一个分形层完整拥有自己的生成、颜色和探索配方；任何参数修改都只替换这一不可变值。</summary>
public sealed record FractalLayerDefinition(
    string Id,
    string Name,
    bool IsVisible,
    double Opacity,
    LayerBlendMode BlendMode,
    LayerTransformDefinition Transform,
    ScalarMaskDefinition? Mask,
    long Seed,
    FractalGeneratorKind GeneratorKind,
    JuliaDefinition Julia,
    MandelbrotDefinition Mandelbrot,
    RecursiveTreeDefinition RecursiveTree,
    LSystemDefinition LSystem,
    StrangeAttractorDefinition StrangeAttractor,
    GradientDefinition Gradient,
    ArtworkExplorationDefinition Exploration)
    : ArtworkLayerDefinition(Id, Name, IsVisible, Opacity, BlendMode, Transform, Mask)
{
    public VariationRecipeDefinition ToVariationRecipe() =>
        new(Seed, GeneratorKind, Julia, Mandelbrot, RecursiveTree, LSystem, StrangeAttractor, Gradient);

    public FractalLayerDefinition ApplyVariationRecipe(VariationRecipeDefinition recipe) => this with
    {
        Seed = recipe.Seed,
        GeneratorKind = recipe.GeneratorKind,
        Julia = recipe.Julia,
        Mandelbrot = recipe.Mandelbrot,
        RecursiveTree = recipe.RecursiveTree,
        LSystem = recipe.LSystem,
        StrangeAttractor = recipe.StrangeAttractor,
        Gradient = recipe.Gradient
    };
}

/// <summary>G0008 只允许一层分组；子项只能是分形层，避免用递归 UI 和递归预算炫技。</summary>
public sealed record LayerGroupDefinition : ArtworkLayerDefinition
{
    public LayerGroupDefinition(
        string id,
        string name,
        bool isVisible,
        double opacity,
        LayerBlendMode blendMode,
        LayerTransformDefinition transform,
        ScalarMaskDefinition? mask,
        IEnumerable<ArtworkLayerDefinition> children)
        : base(id, name, isVisible, opacity, blendMode, transform, mask) =>
        Children = Array.AsReadOnly(children.ToArray());

    /// <summary>
    /// 子项允许已知分形层和不可用占位，以便未来能力缺失时仍能无损保存原树；领域验证器明确禁止再次出现分组。
    /// </summary>
    public IReadOnlyList<ArtworkLayerDefinition> Children { get; init; }

    public bool Equals(LayerGroupDefinition? other) =>
        other is not null && base.Equals(other) && Children.SequenceEqual(other.Children);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(base.GetHashCode());
        foreach (var child in Children)
        {
            hash.Add(child);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// 未安装能力的配置以不解释的 JSON 片段保留。领域层只把它当作不透明文本；只有快照适配器负责读写，
/// 渲染边界会在任何昂贵计算前拒绝包含此占位的作品。
/// </summary>
public sealed record UnavailableLayerDefinition(
    string Id,
    string Name,
    bool IsVisible,
    double Opacity,
    LayerBlendMode BlendMode,
    LayerTransformDefinition Transform,
    ScalarMaskDefinition? Mask,
    string TypeId,
    int Version,
    string OpaquePayload)
    : ArtworkLayerDefinition(Id, Name, IsVisible, Opacity, BlendMode, Transform, Mask);

/// <summary>
/// v8 作品以图层树作为生成器参数的唯一事实来源。兼容属性只是当前选中分形层的不可变投影，
/// 供既有参数编辑器和算法逐步迁移使用；它们不会作为第二份字段进入 v8 快照。
/// </summary>
public sealed record ArtworkDefinition
{
    private IReadOnlyList<ArtworkLayerDefinition> _layers;
    private ArtworkGraphDefinition? _legacyGraphOverride;

    public const int CurrentFormatVersion = 8;

    public ArtworkDefinition(
        int formatVersion,
        CanvasDefinition canvas,
        ArtworkPresentationDefinition presentation,
        IEnumerable<ArtworkLayerDefinition> layers,
        EffectChainDefinition masterEffects)
    {
        FormatVersion = formatVersion;
        Canvas = canvas;
        Presentation = presentation;
        _layers = Array.AsReadOnly(layers.ToArray());
        MasterEffects = masterEffects;
    }

    /// <summary>保留旧调用形状仅用于 v1–v6 解码与现有调用点迁移；内部立即折叠为唯一的分形层。</summary>
    public ArtworkDefinition(
        int formatVersion,
        long seed,
        CanvasDefinition canvas,
        FractalGeneratorKind generatorKind,
        JuliaDefinition julia,
        MandelbrotDefinition mandelbrot,
        RecursiveTreeDefinition recursiveTree,
        LSystemDefinition lSystem,
        GradientDefinition gradient,
        ArtworkPresentationDefinition presentation,
        ArtworkExplorationDefinition exploration,
        ArtworkGraphDefinition graph,
        EffectChainDefinition effects)
        : this(
            CurrentFormatVersion,
            canvas,
            presentation with { SelectedLayerId = "layer-1" },
            [new FractalLayerDefinition(
                "layer-1",
                GeneratorDisplayName(generatorKind),
                true,
                1,
                LayerBlendMode.Normal,
                LayerTransformDefinition.Identity,
                null,
                seed,
                generatorKind,
                julia,
                mandelbrot,
                recursiveTree,
                lSystem,
                CreateDefaultAttractor(),
                gradient,
                exploration)],
            EffectChainDefinition.CreateDefaultMaster())
    {
        // 兼容图只活到旧快照验证完成；v8 编码器不会再次保存它。
        _ = formatVersion;
        _ = effects;
        _legacyGraphOverride = graph;
    }

    public int FormatVersion { get; init; }
    public CanvasDefinition Canvas { get; init; }
    public ArtworkPresentationDefinition Presentation { get; init; }
    public IReadOnlyList<ArtworkLayerDefinition> Layers
    {
        get => _layers;
        init => _layers = Array.AsReadOnly(value.ToArray());
    }

    public EffectChainDefinition MasterEffects { get; init; }

    public FractalLayerDefinition SelectedFractalLayer =>
        ArtworkLayerTree.FindFractal(_layers, Presentation.SelectedLayerId) ??
        ArtworkLayerTree.EnumerateFractals(_layers).First();

    public long Seed { get => SelectedFractalLayer.Seed; init => ReplaceSelected(layer => layer with { Seed = value }); }
    public FractalGeneratorKind GeneratorKind
    {
        get => SelectedFractalLayer.GeneratorKind;
        init => ReplaceSelected(layer => layer with { GeneratorKind = value });
    }
    public JuliaDefinition Julia { get => SelectedFractalLayer.Julia; init => ReplaceSelected(layer => layer with { Julia = value }); }
    public MandelbrotDefinition Mandelbrot { get => SelectedFractalLayer.Mandelbrot; init => ReplaceSelected(layer => layer with { Mandelbrot = value }); }
    public RecursiveTreeDefinition RecursiveTree { get => SelectedFractalLayer.RecursiveTree; init => ReplaceSelected(layer => layer with { RecursiveTree = value }); }
    public LSystemDefinition LSystem { get => SelectedFractalLayer.LSystem; init => ReplaceSelected(layer => layer with { LSystem = value }); }
    public StrangeAttractorDefinition StrangeAttractor
    {
        get => SelectedFractalLayer.StrangeAttractor;
        init => ReplaceSelected(layer => layer with { StrangeAttractor = value });
    }
    public GradientDefinition Gradient { get => SelectedFractalLayer.Gradient; init => ReplaceSelected(layer => layer with { Gradient = value }); }
    public ArtworkExplorationDefinition Exploration
    {
        get => SelectedFractalLayer.Exploration;
        init => ReplaceSelected(layer => layer with { Exploration = value });
    }

    /// <summary>
    /// 新作品始终从图层生成规范图。init 入口只保留给旧格式损坏图测试和迁移前验证，
    /// v7 编码器绝不能把该兼容覆盖重新写入文件。
    /// </summary>
    public ArtworkGraphDefinition Graph
    {
        get => _legacyGraphOverride ?? ArtworkGraphFactory.Create(SelectedFractalLayer);
        init => _legacyGraphOverride = value;
    }
    public EffectChainDefinition Effects => MasterEffects;
    internal bool HasLegacyGraphOverride => _legacyGraphOverride is not null;

    internal ArtworkDefinition ClearLegacyGraphOverride()
    {
        var copy = this with { };
        copy._legacyGraphOverride = null;
        return copy;
    }

    public static ArtworkDefinition CreateDefault() => new(
        CurrentFormatVersion,
        new CanvasDefinition(1200, 800, new RgbaColor(10, 14, 28)),
        new ArtworkPresentationDefinition("生成", false, "layer-1"),
        [CreateDefaultLayer("layer-1", FractalGeneratorKind.Julia)],
        EffectChainDefinition.CreateDefaultMaster());

    /// <summary>从当前作品提取能够独立重放画面的最小配方。</summary>
    public VariationRecipeDefinition ToVariationRecipe() =>
        SelectedFractalLayer.ToVariationRecipe();

    /// <summary>
    /// 把候选配方应用到当前作品；画布、探索收藏和 UI 呈现由当前 Document 保留，避免“采用候选”意外改掉工作区。
    /// </summary>
    public ArtworkDefinition ApplyVariationRecipe(VariationRecipeDefinition recipe) =>
        ReplaceSelectedCopy(layer => layer.ApplyVariationRecipe(recipe));

    /// <summary>
    /// 生成器类型和规范图是一个不可拆分的领域修改。所有 UI、预设和迁移都通过此方法切换，
    /// 防止作品声明 Julia、图中却保留路径节点之类的双事实错误。
    /// </summary>
    public ArtworkDefinition WithGeneratorKind(FractalGeneratorKind generatorKind) =>
        ReplaceSelectedCopy(layer => layer with { GeneratorKind = generatorKind });

    public GeneratorFamily GeneratorFamily => GeneratorKind switch
    {
        FractalGeneratorKind.Julia or FractalGeneratorKind.Mandelbrot => GeneratorFamily.EscapeTime,
        FractalGeneratorKind.StrangeAttractor => GeneratorFamily.Attractor,
        _ => GeneratorFamily.LSystem
    };

    public ArtworkDefinition SelectLayer(string layerId) => this with
    {
        Presentation = Presentation with { SelectedLayerId = layerId }
    };

    public static FractalLayerDefinition CreateDefaultLayer(string id, FractalGeneratorKind kind) => new(
        id,
        GeneratorDisplayName(kind),
        true,
        1,
        LayerBlendMode.Normal,
        LayerTransformDefinition.Identity,
        null,
        20260903,
        kind,
        new JuliaDefinition("0", "0", "3.2", "-0.745", "0.113", 320, false, 96),
        new MandelbrotDefinition("-0.5", "0", "3", 320, false, 96),
        new RecursiveTreeDefinition(9, 2, 27, 0.72, 0.12, 0.28, 3.2),
        CreateDefaultLSystem(),
        CreateDefaultAttractor(),
        kind == FractalGeneratorKind.StrangeAttractor
            ? new GradientDefinition(new RgbaColor(24, 47, 89, 0), new RgbaColor(146, 240, 255), new RgbaColor(0, 0, 0, 0))
            : new GradientDefinition(new RgbaColor(20, 31, 74), new RgbaColor(248, 167, 63), new RgbaColor(3, 5, 12)),
        ArtworkExplorationDefinition.CreateDefault());

    public static StrangeAttractorDefinition CreateDefaultAttractor() => new(
        AttractorFormula.Clifford,
        -1.4,
        1.6,
        1.0,
        0.7,
        256,
        1_000_000,
        1.0,
        1.0,
        true,
        2.4,
        0.8);

    public bool Equals(ArtworkDefinition? other) =>
        other is not null && FormatVersion == other.FormatVersion && Canvas == other.Canvas &&
        Presentation == other.Presentation && MasterEffects == other.MasterEffects && Layers.SequenceEqual(other.Layers);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(FormatVersion);
        hash.Add(Canvas);
        hash.Add(Presentation);
        hash.Add(MasterEffects);
        foreach (var layer in Layers)
        {
            hash.Add(layer);
        }

        return hash.ToHashCode();
    }

    private void ReplaceSelected(Func<FractalLayerDefinition, FractalLayerDefinition> replace) =>
        _layers = ArtworkLayerTree.ReplaceFractal(_layers, SelectedFractalLayer.Id, replace);

    private ArtworkDefinition ReplaceSelectedCopy(Func<FractalLayerDefinition, FractalLayerDefinition> replace) => this with
    {
        Layers = ArtworkLayerTree.ReplaceFractal(_layers, SelectedFractalLayer.Id, replace)
    };

    private static string GeneratorDisplayName(FractalGeneratorKind kind) => kind switch
    {
        FractalGeneratorKind.Julia => "Julia 1",
        FractalGeneratorKind.Mandelbrot => "Mandelbrot 1",
        FractalGeneratorKind.RecursiveTree => "递归树 1",
        FractalGeneratorKind.LSystem => "L-System 1",
        FractalGeneratorKind.StrangeAttractor => "奇异吸引子 1",
        _ => "分形层 1"
    };

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

public static class ArtworkLayerTree
{
    public static IEnumerable<FractalLayerDefinition> EnumerateFractals(IEnumerable<ArtworkLayerDefinition> layers)
    {
        foreach (var layer in layers)
        {
            if (layer is FractalLayerDefinition fractal)
            {
                yield return fractal;
            }
            else if (layer is LayerGroupDefinition group)
            {
                foreach (var child in group.Children.OfType<FractalLayerDefinition>())
                {
                    yield return child;
                }
            }
        }
    }

    public static ArtworkLayerDefinition? Find(IEnumerable<ArtworkLayerDefinition> layers, string id) =>
        layers.FirstOrDefault(layer => layer.Id == id) ??
        layers.OfType<LayerGroupDefinition>().SelectMany(group => group.Children).FirstOrDefault(layer => layer.Id == id);

    public static FractalLayerDefinition? FindFractal(IEnumerable<ArtworkLayerDefinition> layers, string id) =>
        EnumerateFractals(layers).FirstOrDefault(layer => layer.Id == id);

    public static IReadOnlyList<ArtworkLayerDefinition> ReplaceFractal(
        IEnumerable<ArtworkLayerDefinition> layers,
        string id,
        Func<FractalLayerDefinition, FractalLayerDefinition> replace) =>
        Array.AsReadOnly(layers.Select(layer => layer switch
        {
            FractalLayerDefinition fractal when fractal.Id == id => replace(fractal),
            LayerGroupDefinition group when group.Children.OfType<FractalLayerDefinition>().Any(child => child.Id == id) => group with
            {
                Children = Array.AsReadOnly(group.Children.Select(child =>
                    child is FractalLayerDefinition fractal && fractal.Id == id ? replace(fractal) : child).ToArray())
            },
            _ => layer
        }).ToArray());
}
