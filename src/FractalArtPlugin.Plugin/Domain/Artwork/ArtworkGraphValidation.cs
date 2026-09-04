namespace FractalArtPlugin.Domain.Artwork;

public sealed record ArtworkGraphPortDefinition(string Name, ArtworkGraphDataKind DataKind);

public sealed record ArtworkGraphOperationDescriptor(
    ArtworkGraphOperation Operation,
    int Version,
    IReadOnlyList<ArtworkGraphPortDefinition> Inputs,
    ArtworkGraphPortDefinition Output);

public sealed record ArtworkGraphDiagnostic(string Code, string? NodeId, string Message);

public sealed class ArtworkGraphValidationException : Exception
{
    public ArtworkGraphValidationException(IEnumerable<ArtworkGraphDiagnostic> diagnostics)
        : this(diagnostics.ToArray())
    {
    }

    private ArtworkGraphValidationException(ArtworkGraphDiagnostic[] diagnostics)
        : base(CreateMessage(diagnostics)) => Diagnostics = Array.AsReadOnly(diagnostics);

    public IReadOnlyList<ArtworkGraphDiagnostic> Diagnostics { get; }

    private static string CreateMessage(IReadOnlyList<ArtworkGraphDiagnostic> diagnostics) =>
        diagnostics.Count == 0
            ? "内部创作图无效。"
            : $"内部创作图无效：{string.Join("；", diagnostics.Select(item => item.Message))}";
}

public interface IArtworkGraphValidator
{
    IReadOnlyList<ArtworkGraphNodeDefinition> ValidateAndSort(
        ArtworkGraphDefinition graph,
        FractalGeneratorKind generatorKind,
        EffectChainDefinition effects);
}

/// <summary>
/// 创作图唯一的结构验证与拓扑排序入口。验证器只理解节点/端口契约，不执行算法，
/// 因而损坏快照会在进入昂贵计算和缓存之前得到完整、可定位的中文诊断。
/// </summary>
internal sealed class ArtworkGraphValidator : IArtworkGraphValidator
{
    private static readonly IReadOnlyDictionary<ArtworkGraphOperation, ArtworkGraphOperationDescriptor> Descriptors =
        CreateDescriptors();

    public IReadOnlyList<ArtworkGraphNodeDefinition> ValidateAndSort(
        ArtworkGraphDefinition graph,
        FractalGeneratorKind generatorKind,
        EffectChainDefinition effects)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(effects);
        var diagnostics = new List<ArtworkGraphDiagnostic>();
        if (graph.Version != ArtworkGraphDefinition.CurrentVersion)
        {
            diagnostics.Add(new("graph.version", null, $"不支持创作图版本 {graph.Version}。"));
        }

        if (effects.Version != EffectChainDefinition.CurrentVersion)
        {
            diagnostics.Add(new("effects.version", "effects", $"不支持效果链版本 {effects.Version}。"));
        }

        if (effects.Effects.Count != 0)
        {
            diagnostics.Add(new("effects.unsupported", "effects", "G0006 只支持空效果链，作品包含尚未支持的效果。"));
        }

        var nodes = new Dictionary<string, ArtworkGraphNodeDefinition>(StringComparer.Ordinal);
        foreach (var node in graph.Nodes)
        {
            if (!IsValidId(node.Id))
            {
                diagnostics.Add(new("node.id", node.Id, "节点 ID 不能为空，且只能包含 ASCII 字母、数字、连字符或下划线。"));
                continue;
            }

            if (!nodes.TryAdd(node.Id, node))
            {
                diagnostics.Add(new("node.duplicate", node.Id, $"节点 {node.Id} 重复。"));
                continue;
            }

            if (!Enum.IsDefined(node.Operation) || !Descriptors.TryGetValue(node.Operation, out var descriptor))
            {
                diagnostics.Add(new("node.operation", node.Id, $"节点 {node.Id} 使用未知操作 {node.Operation}。"));
            }
            else if (node.Version != descriptor.Version)
            {
                diagnostics.Add(new("node.version", node.Id, $"节点 {node.Id} 的操作版本 {node.Version} 不受支持。"));
            }
        }

        if (nodes.Count == 0)
        {
            diagnostics.Add(new("graph.empty", null, "创作图至少需要一个节点。"));
        }

        if (string.IsNullOrWhiteSpace(graph.OutputNodeId) || !nodes.TryGetValue(graph.OutputNodeId, out var outputNode))
        {
            diagnostics.Add(new("graph.output", graph.OutputNodeId, "创作图输出节点不存在。"));
        }
        else if (outputNode.Operation != ArtworkGraphOperation.Output)
        {
            diagnostics.Add(new("graph.output-kind", outputNode.Id, $"节点 {outputNode.Id} 不是输出操作。"));
        }

        var expectedGenerator = generatorKind switch
        {
            FractalGeneratorKind.Julia => ArtworkGraphOperation.JuliaField,
            FractalGeneratorKind.Mandelbrot => ArtworkGraphOperation.MandelbrotField,
            FractalGeneratorKind.RecursiveTree => ArtworkGraphOperation.RecursiveTreePath,
            FractalGeneratorKind.LSystem => ArtworkGraphOperation.LSystemPath,
            _ => (ArtworkGraphOperation)(-1)
        };
        var generatorNodes = nodes.Values.Where(item => IsGenerator(item.Operation)).ToArray();
        if (generatorNodes.Length != 1 || generatorNodes[0].Operation != expectedGenerator)
        {
            diagnostics.Add(new("graph.generator", generatorNodes.FirstOrDefault()?.Id,
                $"创作图必须且只能包含与 {generatorKind} 匹配的生成节点。"));
        }

        var incoming = nodes.Keys.ToDictionary(id => id, _ => new List<ArtworkGraphConnectionDefinition>(), StringComparer.Ordinal);
        var outgoing = nodes.Keys.ToDictionary(id => id, _ => new List<ArtworkGraphConnectionDefinition>(), StringComparer.Ordinal);
        foreach (var connection in graph.Connections)
        {
            if (!nodes.TryGetValue(connection.SourceNodeId, out var source) ||
                !nodes.TryGetValue(connection.TargetNodeId, out var target))
            {
                diagnostics.Add(new("connection.endpoint", connection.TargetNodeId,
                    $"连接 {connection.SourceNodeId}.{connection.SourcePort} → {connection.TargetNodeId}.{connection.TargetPort} 引用了不存在的节点。"));
                continue;
            }

            if (!Descriptors.TryGetValue(source.Operation, out var sourceDescriptor) ||
                !Descriptors.TryGetValue(target.Operation, out var targetDescriptor))
            {
                continue;
            }

            var targetPort = targetDescriptor.Inputs.SingleOrDefault(item => item.Name == connection.TargetPort);
            if (sourceDescriptor.Output.Name != connection.SourcePort)
            {
                diagnostics.Add(new("connection.source-port", source.Id,
                    $"节点 {source.Id} 没有输出端口 {connection.SourcePort}。"));
                continue;
            }

            if (targetPort is null)
            {
                diagnostics.Add(new("connection.target-port", target.Id,
                    $"节点 {target.Id} 没有输入端口 {connection.TargetPort}。"));
                continue;
            }

            if (sourceDescriptor.Output.DataKind != targetPort.DataKind)
            {
                diagnostics.Add(new("connection.type", target.Id,
                    $"连接到节点 {target.Id}.{targetPort.Name} 的类型不兼容：需要 {targetPort.DataKind}，实际为 {sourceDescriptor.Output.DataKind}。"));
                continue;
            }

            incoming[target.Id].Add(connection);
            outgoing[source.Id].Add(connection);
        }

        foreach (var node in nodes.Values)
        {
            if (!Descriptors.TryGetValue(node.Operation, out var descriptor))
            {
                continue;
            }

            foreach (var input in descriptor.Inputs)
            {
                var count = incoming[node.Id].Count(item => item.TargetPort == input.Name);
                if (count != 1)
                {
                    diagnostics.Add(new(count == 0 ? "node.unconnected" : "node.duplicate-input", node.Id,
                        $"节点 {node.Id} 的输入端口 {input.Name} 必须且只能连接一次，当前为 {count} 次。"));
                }
            }
        }

        ThrowIfInvalid(diagnostics);
        var ordered = TopologicalSort(nodes, incoming, outgoing);
        if (ordered.Count != nodes.Count)
        {
            throw new ArtworkGraphValidationException([
                new("graph.cycle", null, "创作图包含循环依赖；G0006 只允许有向无环处理。")]);
        }

        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(generatorNodes.Select(item => item.Id));
        while (queue.TryDequeue(out var current))
        {
            if (!reachable.Add(current))
            {
                continue;
            }

            foreach (var connection in outgoing[current])
            {
                queue.Enqueue(connection.TargetNodeId);
            }
        }

        var disconnected = nodes.Keys.Where(id => !reachable.Contains(id)).Order(StringComparer.Ordinal).ToArray();
        if (disconnected.Length > 0 || !reachable.Contains(graph.OutputNodeId))
        {
            throw new ArtworkGraphValidationException(disconnected
                .Select(id => new ArtworkGraphDiagnostic("graph.disconnected", id, $"节点 {id} 未连接到生成—输出主链。"))
                .ToArray());
        }

        return ordered;
    }

    internal static ArtworkGraphOperationDescriptor GetDescriptor(ArtworkGraphOperation operation) => Descriptors[operation];

    private static IReadOnlyList<ArtworkGraphNodeDefinition> TopologicalSort(
        IReadOnlyDictionary<string, ArtworkGraphNodeDefinition> nodes,
        IReadOnlyDictionary<string, List<ArtworkGraphConnectionDefinition>> incoming,
        IReadOnlyDictionary<string, List<ArtworkGraphConnectionDefinition>> outgoing)
    {
        var degree = nodes.Keys.ToDictionary(id => id, id => incoming[id].Count, StringComparer.Ordinal);
        var ready = new PriorityQueue<string, string>(StringComparer.Ordinal);
        foreach (var id in degree.Where(item => item.Value == 0).Select(item => item.Key))
        {
            ready.Enqueue(id, id);
        }

        var ordered = new List<ArtworkGraphNodeDefinition>(nodes.Count);
        while (ready.TryDequeue(out var id, out _))
        {
            ordered.Add(nodes[id]);
            foreach (var connection in outgoing[id].OrderBy(item => item.TargetNodeId, StringComparer.Ordinal))
            {
                if (--degree[connection.TargetNodeId] == 0)
                {
                    ready.Enqueue(connection.TargetNodeId, connection.TargetNodeId);
                }
            }
        }

        return ordered;
    }

    private static void ThrowIfInvalid(IReadOnlyList<ArtworkGraphDiagnostic> diagnostics)
    {
        if (diagnostics.Count > 0)
        {
            throw new ArtworkGraphValidationException(diagnostics);
        }
    }

    private static bool IsGenerator(ArtworkGraphOperation operation) => operation is
        ArtworkGraphOperation.JuliaField or ArtworkGraphOperation.MandelbrotField or
        ArtworkGraphOperation.RecursiveTreePath or ArtworkGraphOperation.LSystemPath;

    private static bool IsValidId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 64 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static IReadOnlyDictionary<ArtworkGraphOperation, ArtworkGraphOperationDescriptor> CreateDescriptors()
    {
        static ArtworkGraphOperationDescriptor Source(
            ArtworkGraphOperation operation,
            string port,
            ArtworkGraphDataKind type) => new(operation, 1, [], new(port, type));

        static ArtworkGraphOperationDescriptor Transform(
            ArtworkGraphOperation operation,
            ArtworkGraphDataKind input,
            ArtworkGraphDataKind output) => new(operation, 1, [new("source", input)], new("image", output));

        static ArtworkGraphOperationDescriptor ImageTransform(ArtworkGraphOperation operation) =>
            new(operation, 1, [new("image", ArtworkGraphDataKind.ImageSurface)],
                new("image", ArtworkGraphDataKind.ImageSurface));

        return new Dictionary<ArtworkGraphOperation, ArtworkGraphOperationDescriptor>
        {
            [ArtworkGraphOperation.JuliaField] = Source(ArtworkGraphOperation.JuliaField, "field", ArtworkGraphDataKind.ScalarField),
            [ArtworkGraphOperation.MandelbrotField] = Source(ArtworkGraphOperation.MandelbrotField, "field", ArtworkGraphDataKind.ScalarField),
            [ArtworkGraphOperation.RecursiveTreePath] = Source(ArtworkGraphOperation.RecursiveTreePath, "path", ArtworkGraphDataKind.PathGeometry),
            [ArtworkGraphOperation.LSystemPath] = Source(ArtworkGraphOperation.LSystemPath, "path", ArtworkGraphDataKind.PathGeometry),
            [ArtworkGraphOperation.ScalarGradient] = Transform(ArtworkGraphOperation.ScalarGradient, ArtworkGraphDataKind.ScalarField, ArtworkGraphDataKind.ImageSurface),
            [ArtworkGraphOperation.PathStroke] = Transform(ArtworkGraphOperation.PathStroke, ArtworkGraphDataKind.PathGeometry, ArtworkGraphDataKind.ImageSurface),
            [ArtworkGraphOperation.EffectChain] = ImageTransform(ArtworkGraphOperation.EffectChain),
            [ArtworkGraphOperation.SingleLayerComposition] = ImageTransform(ArtworkGraphOperation.SingleLayerComposition),
            [ArtworkGraphOperation.Output] = ImageTransform(ArtworkGraphOperation.Output)
        };
    }
}
