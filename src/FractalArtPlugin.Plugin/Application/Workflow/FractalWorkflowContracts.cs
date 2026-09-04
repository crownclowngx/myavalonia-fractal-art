namespace FractalArtPlugin.Application.Workflow;

internal sealed record WorkflowFileArtifact(
    string Contract,
    int Version,
    string ProducerPluginId,
    Guid ProducerOperationId,
    string Lifetime,
    string Path,
    string MediaType,
    long ByteLength,
    string Sha256);

public sealed record BlurEffectSettings(bool Enabled = true, double Sigma = 1.5d);

public sealed record BloomEffectSettings(
    bool Enabled = true,
    double Threshold = 0.72d,
    double Sigma = 5d,
    double Strength = 0.8d);

public sealed record GrainEffectSettings(bool Enabled = true, double Amount = 3d, long Seed = 0);

public sealed record ImageLabEffectSettings(
    BlurEffectSettings Blur,
    BloomEffectSettings Bloom,
    GrainEffectSettings Grain)
{
    internal static ImageLabEffectSettings Default { get; } = new(new(), new(), new());

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Blur);
        ArgumentNullException.ThrowIfNull(Bloom);
        ArgumentNullException.ThrowIfNull(Grain);
        RequireRange(Blur.Sigma, 0d, 10d, nameof(Blur.Sigma));
        RequireRange(Bloom.Threshold, 0d, 1d, nameof(Bloom.Threshold));
        RequireRange(Bloom.Sigma, 0.1d, 10d, nameof(Bloom.Sigma));
        RequireRange(Bloom.Strength, 0d, 4d, nameof(Bloom.Strength));
        RequireRange(Grain.Amount, 0d, 100d, nameof(Grain.Amount));
    }

    private static void RequireRange(double value, double minimum, double maximum, string name)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(name, value, $"参数必须是 {minimum}–{maximum} 的有限数值。");
        }
    }
}

public sealed record ImageLabExportResult(string OutputPath, long ByteLength, string Sha256);

internal sealed record ArtifactReleaseResult(bool Released, string? WarningCode);

/// <summary>Fractal 拥有的临时 Artifact 端口；创建者始终负责释放。</summary>
internal interface IFractalWorkflowArtifactStore
{
    Task<WorkflowFileArtifact> CreateAsync(
        ArtworkDefinition artwork,
        Guid operationId,
        string lifetime,
        CancellationToken cancellationToken);

    Task<ArtifactReleaseResult> ReleaseAsync(
        WorkflowFileArtifact artifact,
        bool allowTransient,
        CancellationToken cancellationToken);

    Task CleanupExpiredAsync(CancellationToken cancellationToken);
}

/// <summary>只包装 ImageLab Action 的发现、调用与结果解析，不包含渲染和文件所有权。</summary>
internal interface IImageLabActionClient
{
    bool IsAvailable();

    Task<ImageLabExportResult> ApplyAsync(
        WorkflowFileArtifact source,
        ImageLabEffectSettings effects,
        string outputPath,
        IProgress<int>? progress,
        CancellationToken cancellationToken);
}

public interface IImageLabArtEffectExportCoordinator
{
    bool IsAvailable();

    Task<ImageLabExportResult> ExportAsync(
        ArtworkDefinition artwork,
        ImageLabEffectSettings effects,
        string outputPath,
        IProgress<int>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Art 内一键导出的应用协调器。它在顶层调用 Gateway，并用 finally 释放自身文件；
/// Provider Handler 永远不会依赖本类型，因而不会形成嵌套 Workflow 调用。
/// </summary>
internal sealed class ImageLabArtEffectExportCoordinator(
    IFractalWorkflowArtifactStore artifactStore,
    IImageLabActionClient client) : IImageLabArtEffectExportCoordinator
{
    public bool IsAvailable() => client.IsAvailable();

    public async Task<ImageLabExportResult> ExportAsync(
        ArtworkDefinition artwork,
        ImageLabEffectSettings effects,
        string outputPath,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artwork);
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        effects.Validate();
        if (artwork.Canvas.Width > 4096 || artwork.Canvas.Height > 4096 ||
            (long)artwork.Canvas.Width * artwork.Canvas.Height > 16_777_216)
        {
            throw new InvalidOperationException("ImageLab Workflow 导出的单边不能超过 4096，总像素不能超过 16,777,216。");
        }
        if (!client.IsAvailable())
        {
            throw new InvalidOperationException("ImageLab 艺术效果当前不可用。");
        }

        var artifact = await artifactStore.CreateAsync(
            artwork,
            Guid.NewGuid(),
            FractalWorkflowFileArtifactContract.TransientLifetime,
            cancellationToken).ConfigureAwait(false);
        try
        {
            return await client.ApplyAsync(
                artifact, effects, outputPath, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // ActionClient 返回前已 Dispose Run；此处删除不会与 ImageLab 的读取句柄竞争。
            await artifactStore.ReleaseAsync(
                artifact, allowTransient: true, CancellationToken.None).ConfigureAwait(false);
        }
    }
}

internal interface IWorkflowRecipeCodec
{
    byte[] Encode(ArtworkDefinition artwork);
    ArtworkDefinition Decode(ReadOnlySpan<byte> content);
}

public interface IWorkflowRecipeFiles
{
    Task ExportAsync(ArtworkDefinition artwork, string path, CancellationToken cancellationToken);
    Task<ArtworkDefinition> ReadAsync(string path, CancellationToken cancellationToken);
}

public interface IWorkflowRecipeDialog
{
    Task<string?> PickSavePathAsync(string suggestedName, CancellationToken cancellationToken);
}

public interface IImageLabExportDialog
{
    Task<string?> PickOutputPathAsync(string suggestedName, CancellationToken cancellationToken);
}

internal static class FractalWorkflowFileArtifactContract
{
    internal const string Name = "myavalonia.workflow.file-artifact";
    internal const int Version = 1;
    internal const string PluginId = "myavalonia.plugin.fractal.art";
    internal const string PngMediaType = "image/png";
    internal const string TransientLifetime = "transient";
    internal const string RunLifetime = "run";
    internal const string PersistentLifetime = "persistent";

    internal static string RootPath => Path.Combine(
        Path.GetTempPath(),
        "MyAvaloniaManagement",
        "WorkflowArtifacts");
}
