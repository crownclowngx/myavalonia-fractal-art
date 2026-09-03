namespace FractalArtPlugin.Application;

public sealed record RenderedVariation(
    VariationCandidateDefinition Candidate,
    RgbaImage Image,
    bool FromCache);

public sealed record VariationExplorationResult(
    VariationBatch Batch,
    IReadOnlyList<RenderedVariation> RenderedCandidates);

public interface IVariationExplorer
{
    Task<VariationExplorationResult> ExploreAsync(
        ArtworkDefinition source,
        int candidateCount,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<RenderedVariation>> RenderAsync(
        ArtworkDefinition source,
        IReadOnlyList<VariationCandidateDefinition> candidates,
        CancellationToken cancellationToken);
}

/// <summary>
/// 变体用例把“生成配方”和“渲染缩略图”组合起来，但不拥有 Document 状态。
/// 并发上限固定为 3，缓存只保存最近 64 个可重算缩略图；取消发生时不会返回半批结果，也不会写入作品。
/// </summary>
internal sealed class VariationExplorer(
    IVariationGenerator generator,
    IArtworkRenderPipeline renderPipeline) : IVariationExplorer
{
    private const int MaximumParallelism = 3;
    private const int CacheCapacity = 64;
    private const int ThumbnailMaximumEdge = 240;
    private readonly object _cacheSync = new();
    private readonly SemaphoreSlim _renderGate = new(MaximumParallelism, MaximumParallelism);
    private readonly Dictionary<ThumbnailCacheKey, RgbaImage> _cache = [];
    private readonly Queue<ThumbnailCacheKey> _cacheOrder = [];

    public async Task<VariationExplorationResult> ExploreAsync(
        ArtworkDefinition source,
        int candidateCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var batch = generator.Generate(source, candidateCount);
        var rendered = await RenderAsync(source, batch.Candidates, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new VariationExplorationResult(batch, rendered);
    }

    public async Task<IReadOnlyList<RenderedVariation>> RenderAsync(
        ArtworkDefinition source,
        IReadOnlyList<VariationCandidateDefinition> candidates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(candidates);
        cancellationToken.ThrowIfCancellationRequested();

        var tasks = candidates.Select((candidate, index) =>
            RenderOneAsync(source, candidate, index, _renderGate, cancellationToken)).ToArray();
        var unordered = await Task.WhenAll(tasks).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return unordered.OrderBy(item => item.Index).Select(item => item.Value).ToArray();
    }

    private async Task<(int Index, RenderedVariation Value)> RenderOneAsync(
        ArtworkDefinition source,
        VariationCandidateDefinition candidate,
        int index,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        var ratio = Math.Min(1d, Math.Min(
            (double)ThumbnailMaximumEdge / source.Canvas.Width,
            (double)ThumbnailMaximumEdge / source.Canvas.Height));
        var width = Math.Max(1, (int)Math.Round(source.Canvas.Width * ratio));
        var height = Math.Max(1, (int)Math.Round(source.Canvas.Height * ratio));
        var key = new ThumbnailCacheKey(candidate.Recipe, width, height, RenderContext.CurrentRendererVersion);
        lock (_cacheSync)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                return (index, new RenderedVariation(candidate, cached, true));
            }
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 等待并发槽期间其他批次可能已经填充同一配方，进入昂贵渲染前必须再次检查。
            lock (_cacheSync)
            {
                if (_cache.TryGetValue(key, out var cached))
                {
                    return (index, new RenderedVariation(candidate, cached, true));
                }
            }

            var candidateArtwork = source.ApplyVariationRecipe(candidate.Recipe);
            var previewArtwork = candidateArtwork with
            {
                Canvas = candidateArtwork.Canvas with { Width = width, Height = height },
                Presentation = candidateArtwork.Presentation with { HighQualityPreview = false }
            };
            // 每张缩略图内部使用单线程；与外层 3 路并发组合后，整批不会放大成 3×CPU 的嵌套并行。
            var context = RenderContext.ForPreview(previewArtwork) with { MaxDegreeOfParallelism = 1 };
            var image = await renderPipeline.RenderAsync(candidateArtwork, context, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            AddToCache(key, image);
            return (index, new RenderedVariation(candidate, image, false));
        }
        finally
        {
            gate.Release();
        }
    }

    private void AddToCache(ThumbnailCacheKey key, RgbaImage image)
    {
        lock (_cacheSync)
        {
            if (_cache.ContainsKey(key))
            {
                return;
            }

            _cache.Add(key, image);
            _cacheOrder.Enqueue(key);
            while (_cacheOrder.Count > CacheCapacity)
            {
                _cache.Remove(_cacheOrder.Dequeue());
            }
        }
    }

    private readonly record struct ThumbnailCacheKey(
        VariationRecipeDefinition Recipe,
        int Width,
        int Height,
        int RendererVersion);
}
