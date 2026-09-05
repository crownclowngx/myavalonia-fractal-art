using System.Diagnostics;
using System.Text;

namespace FractalArtPlugin.Application.Workflow;

/// <summary>批次项只描述已保存的作品；调用者不能提交 Host 身份或指定产物写入位置。</summary>
internal sealed record WorkflowBatchItem(string ItemId, string RecipePath);
internal sealed record WorkflowRecipeReadResult(ArtworkDefinition Artwork, int ByteLength);
internal sealed record WorkflowArtifactOrigin(Guid InvocationId, string ItemId);
internal sealed record WorkflowBatchResult(string ItemId, WorkflowFileArtifact Artifact, int Width, int Height);
internal sealed record WorkflowBatchProgress(string Stage, int Percent, string Message);

/// <summary>带实际读取字节数的窄端口，让批预算不依赖读取前可能过期的 FileInfo。</summary>
internal interface IWorkflowBoundedRecipeReader
{
    Task<WorkflowRecipeReadResult> ReadBoundedAsync(string path, int maximumBytes, CancellationToken cancellationToken);
}

internal interface IWorkflowBatchExporter
{
    Task<WorkflowBatchArtifacts> ExportAsync(IReadOnlyList<WorkflowBatchItem> items, Guid invocationId,
        IProgress<WorkflowBatchProgress> progress, CancellationToken cancellationToken);
}

/// <summary>
/// 一次批导出的暂存产物所有权。应用层返回后，JSON 序列化和最后取消检查仍可能失败，
/// 因此由 Handler 显式 Commit 才移交给 Consumer；此前 DisposeAsync 必须尝试回滚每一项。
/// 这不是文件系统多文件原子事务：无法删除的自有文件保留 marker，交由 TTL 恢复。
/// </summary>
internal sealed class WorkflowBatchArtifacts(IFractalWorkflowArtifactStore store) : IAsyncDisposable
{
    private readonly List<WorkflowBatchResult> _results = [];
    private bool _committed;
    public IReadOnlyList<WorkflowBatchResult> Results => _results.AsReadOnly();
    internal void Add(WorkflowBatchResult result) => _results.Add(result);
    internal void Commit() => _committed = true;

    public async ValueTask DisposeAsync()
    {
        if (_committed) return;
        foreach (var result in _results.AsEnumerable().Reverse())
        {
            await RollbackAsync(store, result.Artifact).ConfigureAwait(false);
        }
        _results.Clear();
    }

    internal static async Task RollbackAsync(IFractalWorkflowArtifactStore store, WorkflowFileArtifact artifact)
    {
        try
        {
            // 原调用令牌通常已经取消；复用它会让释放在入口立即退出。
            var release = await store.ReleaseAsync(artifact, false, CancellationToken.None).ConfigureAwait(false);
            if (!release.Released)
                Trace.TraceWarning("Fractal Artifact {0} 回滚延迟，等待 TTL 清理。", artifact.ProducerOperationId);
        }
        catch (Exception)
        {
            // 清理异常不能掩盖渲染/取消原因，也不能阻断其它产物回收；只记录非敏感操作身份。
            Trace.TraceWarning("Fractal Artifact {0} 回滚失败，等待 TTL 清理。", artifact.ProducerOperationId);
        }
    }
}

/// <summary>
/// 批次应用用例：先捕获全部配方并完成预检，再顺序生成。它只依赖读取、计划和存储端口，
/// 不依赖 SDK、Gateway 或 Document；相同的最终质量渲染规则由既有导出计划保证。
/// 外层不并行，避免把单图内部并行和像素缓冲按批次数放大。
/// </summary>
internal sealed class WorkflowBatchExporter(IWorkflowBoundedRecipeReader reader, IArtworkExportPlanner planner,
    IFractalWorkflowArtifactStore store) : IWorkflowBatchExporter
{
    internal const int MaximumItems = 16;
    internal const int MaximumRecipeBytes = 4 * 1024 * 1024;
    internal const int MaximumBatchBytes = 16 * 1024 * 1024;
    internal const long MaximumBatchPixels = 67_108_864;

    public async Task<WorkflowBatchArtifacts> ExportAsync(IReadOnlyList<WorkflowBatchItem> items, Guid invocationId,
        IProgress<WorkflowBatchProgress> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(progress);
        if (invocationId == Guid.Empty) throw new ArgumentException("批次调用身份不能为空。", nameof(invocationId));
        if (items.Count is < 1 or > MaximumItems) throw new InvalidDataException("批次必须包含 1–16 项作品。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.ItemId) || item.ItemId.EnumerateRunes().Count() > 64 ||
                !ids.Add(item.ItemId)) throw new InvalidDataException("批次项身份必须非空、唯一且不超过 64 个字符。");
            if (string.IsNullOrWhiteSpace(item.RecipePath) || item.RecipePath.Length > 32767 ||
                !Path.IsPathFullyQualified(item.RecipePath)) throw new InvalidDataException("配方必须使用有效绝对路径。");
        }

        var plans = new List<ArtworkExportPlan>(items.Count);
        var bytes = 0;
        long pixels = 0;
        for (var index = 0; index < items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new("validating", index * 20 / items.Count, $"正在验证作品 {index + 1}/{items.Count}。"));
            var read = await reader.ReadBoundedAsync(items[index].RecipePath,
                Math.Min(MaximumRecipeBytes, MaximumBatchBytes - bytes), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (read.ByteLength is <= 0 or > MaximumRecipeBytes || read.ByteLength > MaximumBatchBytes - bytes)
                throw new InvalidDataException("批次配方超过 16 MiB 读取预算。");
            bytes += read.ByteLength;
            ValidateCanvas(read.Artwork);
            pixels += (long)read.Artwork.Canvas.Width * read.Artwork.Canvas.Height;
            if (pixels > MaximumBatchPixels) throw new InvalidDataException("批次总输出像素超过 67,108,864。");
            plans.Add(planner.Create(read.Artwork, new(read.Artwork.Canvas.Width, read.Artwork.Canvas.Height, false)));
        }

        var artifacts = new WorkflowBatchArtifacts(store);
        try
        {
            for (var index = 0; index < items.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress.Report(new("rendering", 20 + index * 75 / items.Count, $"正在渲染作品 {index + 1}/{items.Count}。"));
                var artifact = await store.CreateAsync(plans[index].Artwork, Guid.NewGuid(),
                    FractalWorkflowFileArtifactContract.RunLifetime, cancellationToken,
                    new(invocationId, items[index].ItemId)).ConfigureAwait(false);
                // 先登记所有权再观察取消，即使替身/底层在取消后返回，当前文件也不会漏回滚。
                artifacts.Add(new(items[index].ItemId, artifact, plans[index].Request.Width, plans[index].Request.Height));
                cancellationToken.ThrowIfCancellationRequested();
            }
            progress.Report(new("committing", 95, "正在准备批次结果。"));
            cancellationToken.ThrowIfCancellationRequested();
            return artifacts;
        }
        catch
        {
            await artifacts.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static void ValidateCanvas(ArtworkDefinition artwork)
    {
        if (artwork.Canvas.Width is < 1 or > 4096 || artwork.Canvas.Height is < 1 or > 4096 ||
            (long)artwork.Canvas.Width * artwork.Canvas.Height > 16_777_216)
            throw new InvalidDataException("Workflow 渲染的单边不能超过 4096，总像素不能超过 16,777,216。");
    }
}
