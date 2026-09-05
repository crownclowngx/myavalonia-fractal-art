namespace FractalArtPlugin.Application;

public sealed record RenderedVariation(
    VariationCandidateDefinition Candidate,
    ImageSurface Image,
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
/// 并发上限固定为 3；缩略图和中间结果统一使用当前 Document Scope 的创作图缓存，
/// 取消发生时不会返回半批结果，也不会写入作品。
/// </summary>
internal sealed class VariationExplorer(
    IVariationGenerator generator,
    IArtworkRenderPipeline renderPipeline) : IVariationExplorer
{
    private const int MaximumParallelism = 3;
    private const int ThumbnailMaximumEdge = 240;
    private readonly SemaphoreSlim _renderGate = new(MaximumParallelism, MaximumParallelism);

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
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var candidateArtwork = source.ApplyVariationRecipe(candidate.Recipe);
            // 每张缩略图内部使用单线程；与外层 3 路并发组合后，整批不会放大成 3×CPU 的嵌套并行。
            var context = RenderContext.ForThumbnail(candidateArtwork, ThumbnailMaximumEdge);
            var result = await renderPipeline.RenderAsync(candidateArtwork, context, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return (index, new RenderedVariation(candidate, result.Image, result.Execution.FullyFromCache));
        }
        finally
        {
            gate.Release();
        }
    }

}
