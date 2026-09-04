namespace FractalArtPlugin.Domain.Artwork;

/// <summary>
/// 创作图端口允许传递的数据类别。枚举会进入作品文件，只能追加，不能调整已有数值。
/// 这里刻意只描述稳定的领域数据，不暴露 Avalonia、Bitmap 或节点实现类型。
/// </summary>
public enum ArtworkGraphDataKind
{
    ScalarField = 0,
    PathGeometry = 1,
    ImageSurface = 2,
    Mask = 3,
    PointCloud = 4
}

/// <summary>
/// G0006 支持的节点操作。节点图是作品内部实现，不是面向用户的通用工作流语言；
/// 新操作必须显式追加并提供类型描述、版本、执行器和迁移，禁止通过反射猜测行为。
/// </summary>
public enum ArtworkGraphOperation
{
    JuliaField = 0,
    MandelbrotField = 1,
    RecursiveTreePath = 2,
    LSystemPath = 3,
    ScalarGradient = 4,
    PathStroke = 5,
    EffectChain = 6,
    SingleLayerComposition = 7,
    Output = 8,
    StrangeAttractorPoints = 9,
    PointDensity = 10,
    DensityGradient = 11,
    DensityGlow = 12
}

public sealed record ArtworkGraphNodeDefinition(string Id, ArtworkGraphOperation Operation, int Version);

public sealed record ArtworkGraphConnectionDefinition(
    string SourceNodeId,
    string SourcePort,
    string TargetNodeId,
    string TargetPort);

/// <summary>
/// 可持久化的有向无环创作图。集合在构造时复制，避免保存、缓存或执行期间被外部修改。
/// 图只保存关系与节点版本；Julia、渐变等真实参数仍由 <see cref="ArtworkDefinition"/> 唯一持有。
/// </summary>
public sealed record ArtworkGraphDefinition
{
    public const int CurrentVersion = 1;

    public ArtworkGraphDefinition(
        int version,
        IEnumerable<ArtworkGraphNodeDefinition> nodes,
        IEnumerable<ArtworkGraphConnectionDefinition> connections,
        string outputNodeId)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(connections);
        Version = version;
        Nodes = Array.AsReadOnly(nodes.ToArray());
        Connections = Array.AsReadOnly(connections.ToArray());
        OutputNodeId = outputNodeId;
    }

    public int Version { get; }
    public IReadOnlyList<ArtworkGraphNodeDefinition> Nodes { get; }
    public IReadOnlyList<ArtworkGraphConnectionDefinition> Connections { get; }
    public string OutputNodeId { get; }

    public bool Equals(ArtworkGraphDefinition? other) =>
        other is not null &&
        Version == other.Version &&
        string.Equals(OutputNodeId, other.OutputNodeId, StringComparison.Ordinal) &&
        Nodes.SequenceEqual(other.Nodes) &&
        Connections.SequenceEqual(other.Connections);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Version);
        hash.Add(OutputNodeId, StringComparer.Ordinal);
        foreach (var node in Nodes)
        {
            hash.Add(node);
        }

        foreach (var connection in Connections)
        {
            hash.Add(connection);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// 效果的强类型基类。每个效果都携带稳定类型和版本；新增能力必须增加明确派生记录和对应 DTO，
/// 不能退化为字符串参数袋。未知能力由不可用占位保留并在渲染边界统一阻止。
/// </summary>
public abstract record ArtworkEffectDefinition(string TypeId, int Version, bool IsEnabled);

public sealed record ToneEffectDefinition(bool IsEnabled, double Brightness, double Contrast, double Saturation)
    : ArtworkEffectDefinition("tone", 1, IsEnabled);

public sealed record BloomEffectDefinition(bool IsEnabled, double Threshold, double Sigma, double Strength)
    : ArtworkEffectDefinition("bloom", 1, IsEnabled);

public sealed record UnavailableEffectDefinition(
    string TypeId,
    int Version,
    bool IsEnabled,
    string OpaquePayload)
    : ArtworkEffectDefinition(TypeId, Version, IsEnabled);

public sealed record EffectChainDefinition
{
    public const int CurrentVersion = 1;

    public EffectChainDefinition(int version, IEnumerable<ArtworkEffectDefinition> effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        Version = version;
        Effects = Array.AsReadOnly(effects.ToArray());
    }

    public int Version { get; }
    public IReadOnlyList<ArtworkEffectDefinition> Effects { get; }

    public static EffectChainDefinition Empty { get; } = new(CurrentVersion, []);

    public static EffectChainDefinition CreateDefaultMaster() => new(CurrentVersion,
        [new ToneEffectDefinition(false, 0, 0, 1), new BloomEffectDefinition(false, 0.72, 2.4, 0.8)]);

    public bool Equals(EffectChainDefinition? other) =>
        other is not null && Version == other.Version && Effects.SequenceEqual(other.Effects);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Version);
        foreach (var effect in Effects)
        {
            hash.Add(effect);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// 为当前生成器建立唯一的规范图。固定节点身份使保存结果、缓存键和测试诊断稳定，
/// 同时避免 Document 或预设目录分别拼装一套容易漂移的关系。
/// </summary>
public static class ArtworkGraphFactory
{
    public static ArtworkGraphDefinition Create(FractalLayerDefinition layer) => Create(layer.GeneratorKind, layer.Id);

    public static ArtworkGraphDefinition Create(FractalGeneratorKind generatorKind)
        => Create(generatorKind, null);

    private static ArtworkGraphDefinition Create(FractalGeneratorKind generatorKind, string? prefix)
    {
        string NodeId(string suffix) => prefix is null ? suffix : $"{prefix}-{suffix}";
        if (generatorKind == FractalGeneratorKind.StrangeAttractor)
        {
            return new ArtworkGraphDefinition(
                ArtworkGraphDefinition.CurrentVersion,
                [
                    new(NodeId("generator"), ArtworkGraphOperation.StrangeAttractorPoints, 1),
                    new(NodeId("density"), ArtworkGraphOperation.PointDensity, 1),
                    new(NodeId("color"), ArtworkGraphOperation.DensityGradient, 1),
                    new(NodeId("glow"), ArtworkGraphOperation.DensityGlow, 1),
                    new(NodeId("effects"), ArtworkGraphOperation.EffectChain, 1),
                    new(NodeId("composition"), ArtworkGraphOperation.SingleLayerComposition, 1),
                    new(NodeId("output"), ArtworkGraphOperation.Output, 1)
                ],
                [
                    new(NodeId("generator"), "points", NodeId("density"), "source"),
                    new(NodeId("density"), "field", NodeId("color"), "source"),
                    new(NodeId("color"), "image", NodeId("glow"), "image"),
                    new(NodeId("glow"), "image", NodeId("effects"), "image"),
                    new(NodeId("effects"), "image", NodeId("composition"), "image"),
                    new(NodeId("composition"), "image", NodeId("output"), "image")
                ],
                NodeId("output"));
        }

        var (generator, colorizer) = generatorKind switch
        {
            FractalGeneratorKind.Julia => (ArtworkGraphOperation.JuliaField, ArtworkGraphOperation.ScalarGradient),
            FractalGeneratorKind.Mandelbrot => (ArtworkGraphOperation.MandelbrotField, ArtworkGraphOperation.ScalarGradient),
            FractalGeneratorKind.RecursiveTree => (ArtworkGraphOperation.RecursiveTreePath, ArtworkGraphOperation.PathStroke),
            FractalGeneratorKind.LSystem => (ArtworkGraphOperation.LSystemPath, ArtworkGraphOperation.PathStroke),
            _ => throw new ArgumentOutOfRangeException(nameof(generatorKind), generatorKind, "生成器类型非法。")
        };
        var sourcePort = generatorKind is FractalGeneratorKind.Julia or FractalGeneratorKind.Mandelbrot
            ? "field"
            : "path";

        // 保留旧公开工厂的 generator/color 等节点 ID，供 v6 图验证与迁移使用；
        // v7 起每层图都加稳定层 ID 前缀，避免多个生成器共享一个 Document 缓存时发生键冲突。
        return new ArtworkGraphDefinition(
            ArtworkGraphDefinition.CurrentVersion,
            [
                new(NodeId("generator"), generator, 1),
                new(NodeId("color"), colorizer, 1),
                new(NodeId("effects"), ArtworkGraphOperation.EffectChain, 1),
                new(NodeId("composition"), ArtworkGraphOperation.SingleLayerComposition, 1),
                new(NodeId("output"), ArtworkGraphOperation.Output, 1)
            ],
            [
                new(NodeId("generator"), sourcePort, NodeId("color"), "source"),
                new(NodeId("color"), "image", NodeId("effects"), "image"),
                new(NodeId("effects"), "image", NodeId("composition"), "image"),
                new(NodeId("composition"), "image", NodeId("output"), "image")
            ],
            NodeId("output"));
    }
}
