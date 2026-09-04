using System.Security.Cryptography;
using System.Text;

namespace FractalArtPlugin.Application;

public sealed record ArtworkRenderExecutionSummary(
    IReadOnlyList<string> CacheHitNodeIds,
    IReadOnlyList<string> ExecutedNodeIds,
    int CacheableNodeCount)
{
    public bool FullyFromCache => CacheableNodeCount > 0 && CacheHitNodeIds.Count == CacheableNodeCount;
}

public sealed record ArtworkRenderResult(ImageSurface Image, ArtworkRenderExecutionSummary Execution);

public sealed class ArtworkGraphExecutionException(
    string nodeId,
    ArtworkGraphOperation operation,
    Exception innerException)
    : InvalidOperationException($"执行创作图节点 {nodeId}（{operation}）失败：{innerException.Message}", innerException)
{
    public string NodeId { get; } = nodeId;
    public ArtworkGraphOperation Operation { get; } = operation;
}

internal readonly record struct ArtworkNodeCacheKey(string Digest);

internal interface IArtworkGraphCache : IDisposable
{
    bool TryGet(ArtworkNodeCacheKey key, out ArtworkGraphValue value);
    void Set(ArtworkNodeCacheKey key, ArtworkGraphValue value);
    void Clear();
}

/// <summary>
/// 每个 Document Scope 独占的有界 LRU。锁只保护索引与链表，不包围昂贵计算；
/// 因而相同冷请求可以各自计算，一个调用方的取消不会传播给另一个调用方。
/// </summary>
internal sealed class ArtworkGraphCache : IArtworkGraphCache
{
    internal const long DefaultMaximumBytes = 128L * 1024 * 1024;
    internal const int DefaultMaximumEntries = 256;
    private readonly object _sync = new();
    private readonly long _maximumBytes;
    private readonly int _maximumEntries;
    private readonly Dictionary<ArtworkNodeCacheKey, CacheEntry> _entries = [];
    private readonly LinkedList<ArtworkNodeCacheKey> _recency = [];
    private long _currentBytes;
    private bool _disposed;

    public ArtworkGraphCache() : this(DefaultMaximumBytes, DefaultMaximumEntries)
    {
    }

    internal ArtworkGraphCache(long maximumBytes, int maximumEntries)
    {
        if (maximumBytes <= 0 || maximumEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes), "缓存容量必须为正数。");
        }

        _maximumBytes = maximumBytes;
        _maximumEntries = maximumEntries;
    }

    internal int Count
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    internal long CurrentBytes
    {
        get
        {
            lock (_sync)
            {
                return _currentBytes;
            }
        }
    }

    public bool TryGet(ArtworkNodeCacheKey key, out ArtworkGraphValue value)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(key, out var entry))
            {
                value = null!;
                return false;
            }

            _recency.Remove(entry.RecencyNode);
            _recency.AddLast(entry.RecencyNode);
            value = entry.Value;
            return true;
        }
    }

    public void Set(ArtworkNodeCacheKey key, ArtworkGraphValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.EstimatedByteSize <= 0 || value.EstimatedByteSize > _maximumBytes)
        {
            return;
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(key, out var existing))
            {
                _recency.Remove(existing.RecencyNode);
                _recency.AddLast(existing.RecencyNode);
                return;
            }

            var node = _recency.AddLast(key);
            _entries.Add(key, new CacheEntry(value, node));
            _currentBytes = checked(_currentBytes + value.EstimatedByteSize);
            while (_entries.Count > _maximumEntries || _currentBytes > _maximumBytes)
            {
                EvictOldest();
            }
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
            _recency.Clear();
            _currentBytes = 0;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _entries.Clear();
            _recency.Clear();
            _currentBytes = 0;
        }
    }

    private void EvictOldest()
    {
        var oldest = _recency.First;
        if (oldest is null)
        {
            return;
        }

        _recency.RemoveFirst();
        var removed = _entries[oldest.Value];
        _entries.Remove(oldest.Value);
        _currentBytes -= removed.Value.EstimatedByteSize;
    }

    private sealed record CacheEntry(ArtworkGraphValue Value, LinkedListNode<ArtworkNodeCacheKey> RecencyNode);
}

internal interface IArtworkGraphExecutor
{
    Task<ArtworkRenderResult> ExecuteAsync(
        ArtworkDefinition artwork,
        RenderContext context,
        CancellationToken cancellationToken);

    Task<(ScalarField Field, ArtworkRenderExecutionSummary Execution)> ExecuteScalarAsync(
        ArtworkDefinition artwork,
        RenderContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// 先验证、再按稳定拓扑顺序执行节点，并以内容身份做 cache-aside。
/// 缓存写入发生在节点成功且取消检查通过之后；错误、取消和半成品不会成为后续请求的命中结果。
/// </summary>
internal sealed class ArtworkGraphExecutor : IArtworkGraphExecutor
{
    private readonly IArtworkGraphValidator _validator;
    private readonly IArtworkGraphCache _cache;
    private readonly IReadOnlyDictionary<ArtworkGraphOperation, IArtworkGraphNodeExecutor> _executors;

    public ArtworkGraphExecutor(
        IArtworkGraphValidator validator,
        IArtworkGraphCache cache,
        IEnumerable<IArtworkGraphNodeExecutor> executors)
    {
        _validator = validator;
        _cache = cache;
        try
        {
            _executors = executors.ToDictionary(item => item.Operation);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("每种创作图操作必须且只能登记一个执行器。", exception);
        }
    }

    public async Task<ArtworkRenderResult> ExecuteAsync(
        ArtworkDefinition artwork,
        RenderContext context,
        CancellationToken cancellationToken)
    {
        var ordered = _validator.ValidateAndSort(artwork.Graph, artwork.GeneratorKind, EffectChainDefinition.Empty);
        cancellationToken.ThrowIfCancellationRequested();
        var values = new Dictionary<string, ArtworkGraphValue>(StringComparer.Ordinal);
        var keys = new Dictionary<string, ArtworkNodeCacheKey>(StringComparer.Ordinal);
        var remainingConsumers = artwork.Graph.Connections
            .GroupBy(connection => connection.SourceNodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var cacheHits = new List<string>();
        var executed = new List<string>();
        var cacheableCount = 0;

        foreach (var node in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_executors.TryGetValue(node.Operation, out var executor))
            {
                throw new InvalidOperationException($"没有登记创作图操作 {node.Operation} 的执行器。");
            }

            var nodeInputs = new Dictionary<string, ArtworkGraphValue>(StringComparer.Ordinal);
            var inputKeys = new Dictionary<string, ArtworkNodeCacheKey>(StringComparer.Ordinal);
            foreach (var connection in artwork.Graph.Connections.Where(item => item.TargetNodeId == node.Id))
            {
                nodeInputs.Add(connection.TargetPort, values[connection.SourceNodeId]);
                inputKeys.Add(connection.TargetPort, keys[connection.SourceNodeId]);
            }

            var key = ArtworkGraphCacheKeyFactory.Create(artwork.Graph.Version, node, artwork, context, inputKeys);
            keys.Add(node.Id, key);
            var cacheable = IsCacheable(node.Operation);
            if (cacheable)
            {
                cacheableCount++;
                if (_cache.TryGet(key, out var cached))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    values.Add(node.Id, cached);
                    cacheHits.Add(node.Id);
                    ReleaseConsumedInputs(artwork.Graph, node.Id, values, remainingConsumers);
                    continue;
                }
            }

            try
            {
                var value = await executor.ExecuteAsync(artwork, context, nodeInputs, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var expected = ArtworkGraphValidator.GetDescriptor(node.Operation).Output.DataKind;
                if (value.DataKind != expected)
                {
                    throw new InvalidOperationException($"节点返回 {value.DataKind}，但端口声明为 {expected}。");
                }

                values.Add(node.Id, value);
                executed.Add(node.Id);
                if (cacheable)
                {
                    _cache.Set(key, value);
                }

                // 非缓存中间值只活到最后一个消费者。键摘要很小且可能仍参与后续输入身份，
                // 因而只及时释放可能很大的 ScalarField、Path 或 ImageSurface 实例。
                ReleaseConsumedInputs(artwork.Graph, node.Id, values, remainingConsumers);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not ArtworkGraphExecutionException)
            {
                throw new ArtworkGraphExecutionException(node.Id, node.Operation, exception);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!values.TryGetValue(artwork.Graph.OutputNodeId, out var output) || output is not ImageSurfaceGraphValue image)
        {
            throw new InvalidOperationException("创作图没有产生 ImageSurface 输出。");
        }

        return new ArtworkRenderResult(
            image.Value,
            new ArtworkRenderExecutionSummary(cacheHits.AsReadOnly(), executed.AsReadOnly(), cacheableCount));
    }

    /// <summary>
    /// 遮罩只求值生成节点，并与完整图使用完全相同的节点键和 Document Scope 缓存。
    /// 隐藏遮罩源因此不会额外执行着色、效果和合成节点；稍后恢复可见时又能直接复用标量场。
    /// </summary>
    public async Task<(ScalarField Field, ArtworkRenderExecutionSummary Execution)> ExecuteScalarAsync(
        ArtworkDefinition artwork,
        RenderContext context,
        CancellationToken cancellationToken)
    {
        var graph = artwork.Graph;
        var generatorNode = _validator.ValidateAndSort(graph, artwork.GeneratorKind, EffectChainDefinition.Empty)
            .Single(node => node.Operation is ArtworkGraphOperation.JuliaField or ArtworkGraphOperation.MandelbrotField);
        cancellationToken.ThrowIfCancellationRequested();
        var key = ArtworkGraphCacheKeyFactory.Create(graph.Version, generatorNode, artwork, context,
            new Dictionary<string, ArtworkNodeCacheKey>());
        if (_cache.TryGet(key, out var cached) && cached is ScalarFieldGraphValue cachedField)
        {
            return (cachedField.Value, new ArtworkRenderExecutionSummary([generatorNode.Id], [], 1));
        }

        var executor = _executors[generatorNode.Operation];
        ArtworkGraphValue value;
        try
        {
            value = await executor.ExecuteAsync(
                artwork, context, new Dictionary<string, ArtworkGraphValue>(), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ArtworkGraphExecutionException(generatorNode.Id, generatorNode.Operation, exception);
        }

        if (value is not ScalarFieldGraphValue field)
        {
            throw new InvalidOperationException($"节点 {generatorNode.Id} 没有产生 ScalarField。");
        }

        _cache.Set(key, value);
        return (field.Value, new ArtworkRenderExecutionSummary([], [generatorNode.Id], 1));
    }

    private static bool IsCacheable(ArtworkGraphOperation operation) => operation is
        ArtworkGraphOperation.JuliaField or ArtworkGraphOperation.MandelbrotField or
        ArtworkGraphOperation.RecursiveTreePath or ArtworkGraphOperation.LSystemPath or
        ArtworkGraphOperation.ScalarGradient or ArtworkGraphOperation.PathStroke;

    private static void ReleaseConsumedInputs(
        ArtworkGraphDefinition graph,
        string consumerNodeId,
        Dictionary<string, ArtworkGraphValue> values,
        Dictionary<string, int> remainingConsumers)
    {
        foreach (var connection in graph.Connections.Where(item => item.TargetNodeId == consumerNodeId))
        {
            var remaining = --remainingConsumers[connection.SourceNodeId];
            if (remaining == 0 && connection.SourceNodeId != graph.OutputNodeId)
            {
                values.Remove(connection.SourceNodeId);
            }
        }
    }
}

/// <summary>
/// 对每个节点显式写入它真正读取的参数，再加上输入节点摘要和版本信息。
/// 二进制写入使用长度前缀，避免简单字符串拼接的边界歧义，最终以 SHA-256 作为字典键。
/// </summary>
internal static class ArtworkGraphCacheKeyFactory
{
    public static ArtworkNodeCacheKey Create(
        int graphVersion,
        ArtworkGraphNodeDefinition node,
        ArtworkDefinition artwork,
        RenderContext context,
        IReadOnlyDictionary<string, ArtworkNodeCacheKey> inputs)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(graphVersion);
        writer.Write(node.Id);
        writer.Write((int)node.Operation);
        writer.Write(node.Version);
        writer.Write(context.RendererVersion);
        foreach (var input in inputs.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            writer.Write(input.Key);
            writer.Write(input.Value.Digest);
        }

        switch (node.Operation)
        {
            case ArtworkGraphOperation.JuliaField:
                WriteJulia(writer, artwork.Julia);
                WriteRasterContext(writer, context);
                break;
            case ArtworkGraphOperation.MandelbrotField:
                WriteMandelbrot(writer, artwork.Mandelbrot);
                WriteRasterContext(writer, context);
                break;
            case ArtworkGraphOperation.RecursiveTreePath:
                WriteTree(writer, artwork.RecursiveTree);
                writer.Write(artwork.Seed);
                break;
            case ArtworkGraphOperation.LSystemPath:
                WriteLSystem(writer, artwork.LSystem);
                break;
            case ArtworkGraphOperation.ScalarGradient:
                WriteGradient(writer, artwork.Gradient);
                break;
            case ArtworkGraphOperation.PathStroke:
                WriteGradient(writer, artwork.Gradient);
                WriteColor(writer, artwork.Canvas.Background);
                writer.Write(context.Width);
                writer.Write(context.Height);
                writer.Write((int)context.Quality);
                if (artwork.GeneratorKind == FractalGeneratorKind.LSystem)
                {
                    writer.Write(artwork.LSystem.StrokeWidth);
                    writer.Write(artwork.LSystem.StrokeWidthDecay);
                    writer.Write("l-system");
                }
                else
                {
                    writer.Write(artwork.RecursiveTree.StrokeWidth);
                    writer.Write(0.82d);
                    writer.Write("recursive-tree");
                }

                break;
            case ArtworkGraphOperation.EffectChain:
                writer.Write(artwork.Effects.Version);
                writer.Write(artwork.Effects.Effects.Count);
                break;
            case ArtworkGraphOperation.SingleLayerComposition:
                WriteColor(writer, artwork.Canvas.Background);
                break;
            case ArtworkGraphOperation.Output:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(node), node.Operation, "未知创作图操作。");
        }

        writer.Flush();
        return new ArtworkNodeCacheKey(Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)))));
    }

    private static void WriteRasterContext(BinaryWriter writer, RenderContext context)
    {
        writer.Write(context.Width);
        writer.Write(context.Height);
        writer.Write((int)context.Quality);
        writer.Write((int)context.NumericPrecision);
        writer.Write(context.ConfiguredPrecisionDigits);
        writer.Write(context.EffectivePrecisionDigits);
        writer.Write((int)context.KernelPreference);
        // 标量场携带本次调度诊断；即使像素理论上相同，也不能让缓存返回另一套线程/分块说明。
        writer.Write(context.MaxDegreeOfParallelism);
        writer.Write(context.ChunkHeight);
        writer.Write(context.CancellationCheckInterval);
    }

    private static void WriteJulia(BinaryWriter writer, JuliaDefinition value)
    {
        writer.Write(value.CenterX);
        writer.Write(value.CenterY);
        writer.Write(value.Scale);
        writer.Write(value.ConstantReal);
        writer.Write(value.ConstantImaginary);
        writer.Write(value.MaxIterations);
        writer.Write(value.ForceHighPrecision);
        writer.Write(value.PrecisionDigits);
    }

    private static void WriteMandelbrot(BinaryWriter writer, MandelbrotDefinition value)
    {
        writer.Write(value.CenterX);
        writer.Write(value.CenterY);
        writer.Write(value.Scale);
        writer.Write(value.MaxIterations);
        writer.Write(value.ForceHighPrecision);
        writer.Write(value.PrecisionDigits);
    }

    private static void WriteTree(BinaryWriter writer, RecursiveTreeDefinition value)
    {
        writer.Write(value.Depth);
        writer.Write(value.Branches);
        writer.Write(value.BranchAngleDegrees);
        writer.Write(value.LengthDecay);
        writer.Write(value.Randomness);
        writer.Write(value.TrunkLength);
    }

    private static void WriteLSystem(BinaryWriter writer, LSystemDefinition value)
    {
        writer.Write(value.Axiom);
        writer.Write(value.Rules.Count);
        foreach (var rule in value.Rules)
        {
            writer.Write(rule.Symbol);
            writer.Write(rule.Replacement);
        }

        writer.Write(value.Iterations);
        writer.Write(value.TurnAngleDegrees);
        writer.Write(value.InitialHeadingDegrees);
        writer.Write(value.StepLength);
        writer.Write(value.LengthDecay);
    }

    private static void WriteGradient(BinaryWriter writer, GradientDefinition value)
    {
        WriteColor(writer, value.Start);
        WriteColor(writer, value.End);
        WriteColor(writer, value.Interior);
    }

    private static void WriteColor(BinaryWriter writer, RgbaColor value)
    {
        writer.Write(value.Red);
        writer.Write(value.Green);
        writer.Write(value.Blue);
        writer.Write(value.Alpha);
    }
}
