using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Platform.Storage;
using FractalArtPlugin.Application;
using FractalArtPlugin.Application.Workflow;
using MyAvaloniaManagement.PluginSdk;
using MyAvaloniaManagement.PluginSdk.UI;

namespace FractalArtPlugin.Infrastructure.Workflow;

internal sealed class WorkflowRecipeCodec(IArtworkSnapshotCodec artworkCodec) : IWorkflowRecipeCodec
{
    private const int SchemaVersion = 1;

    public byte[] Encode(ArtworkDefinition artwork)
    {
        ArgumentNullException.ThrowIfNull(artwork);
        var content = artworkCodec.Encode(artwork);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = SchemaVersion,
            artworkSchemaVersion = content.SchemaVersion,
            artwork = content.Payload,
        });
    }

    public ArtworkDefinition Decode(ReadOnlySpan<byte> content)
    {
        using var document = JsonDocument.Parse(content.ToArray(), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16,
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Workflow 配方根必须是对象。");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new InvalidDataException("Workflow 配方包含重复字段。");
            }
            if (property.Name is not ("schemaVersion" or "artworkSchemaVersion" or "artwork"))
            {
                throw new InvalidDataException("Workflow 配方包含未知字段。");
            }
        }
        if (names.Count != 3 ||
            !document.RootElement.TryGetProperty("schemaVersion", out var schema) ||
            !schema.TryGetInt32(out var schemaVersion) || schemaVersion != SchemaVersion ||
            !document.RootElement.TryGetProperty("artworkSchemaVersion", out var artworkSchema) ||
            !artworkSchema.TryGetInt32(out var artworkSchemaVersion) ||
            !document.RootElement.TryGetProperty("artwork", out var artwork) ||
            artwork.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Workflow 配方版本或必填字段无效。");
        }
        return artworkCodec.Decode(new DocumentContent(artworkSchemaVersion, artwork.Clone()));
    }
}

internal sealed class WorkflowRecipeFiles(
    IWorkflowRecipeCodec codec,
    IAtomicFileWriter writer) : IWorkflowRecipeFiles
{
    internal const int MaximumBytes = 4 * 1024 * 1024;

    public async Task ExportAsync(
        ArtworkDefinition artwork,
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var bytes = codec.Encode(artwork);
        if (bytes.Length > MaximumBytes)
        {
            throw new InvalidDataException("Workflow 配方超过 4 MiB 上限。");
        }
        await writer.WriteAsync(path, bytes, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ArtworkDefinition> ReadAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var information = new FileInfo(fullPath);
        if (!information.Exists || information.Length is <= 0 or > MaximumBytes)
        {
            throw new InvalidDataException("Workflow 配方不存在、为空或超过 4 MiB 上限。");
        }
        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        return codec.Decode(bytes);
    }
}

/// <summary>把最终质量作品物化成 Fractal 自己拥有的 PNG Artifact。</summary>
internal sealed class FractalWorkflowArtifactStore(
    IArtworkExporter exporter,
    IArtworkExportPlanner exportPlanner)
    : IFractalWorkflowArtifactStore
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions MarkerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
    };

    public async Task<WorkflowFileArtifact> CreateAsync(
        ArtworkDefinition artwork,
        Guid operationId,
        string lifetime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artwork);
        if (operationId == Guid.Empty ||
            lifetime is not (FractalWorkflowFileArtifactContract.TransientLifetime or
                FractalWorkflowFileArtifactContract.RunLifetime))
        {
            throw new ArgumentException("Artifact 操作身份或生命周期无效。");
        }
        await CleanupExpiredAsync(cancellationToken).ConfigureAwait(false);
        var operationRoot = OperationRoot(operationId);
        EnsureArtifactRoots(create: true);
        Directory.CreateDirectory(operationRoot);
        RejectReparsePoint(operationRoot);
        var markerPath = Path.Combine(operationRoot, ".owner.json");
        var sourcePath = Path.Combine(operationRoot, "source.png");
        try
        {
            var marker = new OwnerMarker(
                FractalWorkflowFileArtifactContract.Name,
                FractalWorkflowFileArtifactContract.Version,
                FractalWorkflowFileArtifactContract.PluginId,
                operationId,
                DateTimeOffset.UtcNow);
            var markerBytes = JsonSerializer.SerializeToUtf8Bytes(marker, MarkerOptions);
            await File.WriteAllBytesAsync(markerPath, markerBytes, cancellationToken).ConfigureAwait(false);
            var plan = exportPlanner.Create(
                artwork,
                new ArtworkExportRequest(artwork.Canvas.Width, artwork.Canvas.Height, false));
            await exporter.ExportAsync(plan, sourcePath, cancellationToken).ConfigureAwait(false);
            var information = new FileInfo(sourcePath);
            await using var stream = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return new WorkflowFileArtifact(
                FractalWorkflowFileArtifactContract.Name,
                FractalWorkflowFileArtifactContract.Version,
                FractalWorkflowFileArtifactContract.PluginId,
                operationId,
                lifetime,
                sourcePath,
                FractalWorkflowFileArtifactContract.PngMediaType,
                information.Length,
                Convert.ToHexString(hash));
        }
        catch
        {
            TryDeleteOwnedDirectory(operationRoot);
            throw;
        }
    }

    public Task<ArtifactReleaseResult> ReleaseAsync(
        WorkflowFileArtifact artifact,
        bool allowTransient,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOwnedArtifact(artifact, allowTransient);
        var operationRoot = OperationRoot(artifact.ProducerOperationId);
        try
        {
            if (!Directory.Exists(operationRoot))
            {
                return Task.FromResult(new ArtifactReleaseResult(true, null));
            }
            EnsureArtifactRoots(create: false);
            RejectReparsePoint(operationRoot);
            ValidateMarker(operationRoot, artifact.ProducerOperationId);
            return Task.FromResult(TryDeleteOwnedDirectory(operationRoot)
                ? new ArtifactReleaseResult(true, null)
                : new ArtifactReleaseResult(false, "cleanup_deferred"));
        }
        catch (IOException)
        {
            return Task.FromResult(new ArtifactReleaseResult(false, "cleanup_deferred"));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(new ArtifactReleaseResult(false, "cleanup_deferred"));
        }
    }

    public Task CleanupExpiredAsync(CancellationToken cancellationToken) =>
        CleanupExpiredFilesAsync(cancellationToken);

    /// <summary>
    /// 执行不依赖作品渲染 Scope 的恢复性清理。之所以保留静态入口，是因为插件生命周期属于
    /// Host 根作用域，而 Artifact 创建需要 Scoped 的渲染管线；把两者强行放进同一单例会形成
    /// captive dependency。生命周期与创建路径复用这段纯文件系统逻辑，职责和 DI 生命周期均清晰。
    /// </summary>
    internal static Task CleanupExpiredFilesAsync(CancellationToken cancellationToken)
    {
        var pluginRoot = PluginRoot();
        if (!Directory.Exists(pluginRoot))
        {
            return Task.CompletedTask;
        }
        EnsureArtifactRoots(create: false);
        foreach (var directory in Directory.EnumerateDirectories(pluginRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(directory), "D", out var operationId))
            {
                continue;
            }
            try
            {
                RejectReparsePoint(directory);
                var marker = ReadMarker(directory);
                if (marker.Contract == FractalWorkflowFileArtifactContract.Name &&
                    marker.Version == FractalWorkflowFileArtifactContract.Version &&
                    marker.ProducerPluginId == FractalWorkflowFileArtifactContract.PluginId &&
                    marker.ProducerOperationId == operationId &&
                    marker.CreatedAtUtc <= DateTimeOffset.UtcNow - Retention)
                {
                    _ = TryDeleteOwnedDirectory(directory);
                }
            }
            catch (Exception exception) when (exception is IOException or
                                               UnauthorizedAccessException or
                                               JsonException or InvalidDataException)
            {
                // 清理是恢复性工作，单个损坏目录不能阻断插件加载或新的用户导出。
            }
        }
        return Task.CompletedTask;
    }

    private static void ValidateOwnedArtifact(WorkflowFileArtifact artifact, bool allowTransient)
    {
        if (artifact.Contract != FractalWorkflowFileArtifactContract.Name ||
            artifact.Version != FractalWorkflowFileArtifactContract.Version ||
            artifact.ProducerPluginId != FractalWorkflowFileArtifactContract.PluginId ||
            artifact.ProducerOperationId == Guid.Empty ||
            artifact.MediaType != FractalWorkflowFileArtifactContract.PngMediaType ||
            artifact.Lifetime != FractalWorkflowFileArtifactContract.RunLifetime &&
            !(allowTransient && artifact.Lifetime == FractalWorkflowFileArtifactContract.TransientLifetime))
        {
            throw new InvalidDataException("Artifact 不属于允许释放的 Fractal 文件。");
        }
        var expected = Path.Combine(OperationRoot(artifact.ProducerOperationId), "source.png");
        if (!string.Equals(expected, Path.GetFullPath(artifact.Path), PathComparison))
        {
            throw new InvalidDataException("Artifact 路径与操作身份不匹配。");
        }
    }

    private static void ValidateMarker(string operationRoot, Guid operationId)
    {
        var marker = ReadMarker(operationRoot);
        if (marker.Contract != FractalWorkflowFileArtifactContract.Name ||
            marker.Version != FractalWorkflowFileArtifactContract.Version ||
            marker.ProducerPluginId != FractalWorkflowFileArtifactContract.PluginId ||
            marker.ProducerOperationId != operationId)
        {
            throw new InvalidDataException("Artifact 所有权标记不匹配。");
        }
    }

    private static OwnerMarker ReadMarker(string operationRoot)
    {
        var markerPath = Path.Combine(operationRoot, ".owner.json");
        var information = new FileInfo(markerPath);
        if (!information.Exists || information.Length is <= 0 or > 4096)
        {
            throw new InvalidDataException("Artifact 所有权标记不存在或超限。");
        }
        return JsonSerializer.Deserialize<OwnerMarker>(File.ReadAllBytes(markerPath), MarkerOptions) ??
               throw new InvalidDataException("Artifact 所有权标记无法解析。");
    }

    private static bool TryDeleteOwnedDirectory(string operationRoot)
    {
        RejectReparsePoint(operationRoot);
        foreach (var file in Directory.EnumerateFiles(operationRoot))
        {
            var name = Path.GetFileName(file);
            if (name is not (".owner.json" or "source.png") &&
                !(name.StartsWith(".source.png.", StringComparison.Ordinal) &&
                  name.EndsWith(".tmp", StringComparison.Ordinal)))
            {
                return false;
            }
            RejectReparsePoint(file);
        }
        foreach (var file in Directory.EnumerateFiles(operationRoot))
        {
            File.Delete(file);
        }
        Directory.Delete(operationRoot, recursive: false);
        return true;
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Workflow Artifact 路径不能是重解析点。");
        }
    }

    /// <summary>
    /// 逐层验证公共 Artifact 根和 Fractal 私有根。只验证最终 operation 目录不够：若父目录是 junction，
    /// 规范化字符串仍可能位于约定前缀下，但真实写入或删除已经被重定向到根目录之外。
    /// </summary>
    private static void EnsureArtifactRoots(bool create)
    {
        var artifactRoot = Path.GetFullPath(FractalWorkflowFileArtifactContract.RootPath);
        var pluginRoot = PluginRoot();
        if (create)
        {
            Directory.CreateDirectory(artifactRoot);
            RejectReparsePoint(artifactRoot);
            Directory.CreateDirectory(pluginRoot);
            RejectReparsePoint(pluginRoot);
            return;
        }
        RejectReparsePoint(artifactRoot);
        RejectReparsePoint(pluginRoot);
    }

    private static string PluginRoot() => Path.GetFullPath(Path.Combine(
        FractalWorkflowFileArtifactContract.RootPath,
        FractalWorkflowFileArtifactContract.PluginId));

    private static string OperationRoot(Guid operationId) => Path.GetFullPath(Path.Combine(
        PluginRoot(), operationId.ToString("D")));

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private sealed record OwnerMarker(
        string Contract,
        int Version,
        string ProducerPluginId,
        Guid ProducerOperationId,
        DateTimeOffset CreatedAtUtc);
}

internal sealed class ImageLabActionClient(IWorkflowActionGateway gateway) : IImageLabActionClient
{
    internal static readonly WorkflowActionId ActionId =
        new("myavalonia.plugin.image.lab.workflow.apply-art-effects-file");

    public bool IsAvailable() => gateway.GetAvailableActions().Any(item => item.Id == ActionId);

    public async Task<ImageLabExportResult> ApplyAsync(
        WorkflowFileArtifact source,
        ImageLabEffectSettings effects,
        string outputPath,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(effects);
        effects.Validate();
        var arguments = JsonSerializer.SerializeToElement(new
        {
            source = ArtifactJson.ToObject(source),
            blur = new { enabled = effects.Blur.Enabled, sigma = effects.Blur.Sigma },
            bloom = new
            {
                enabled = effects.Bloom.Enabled,
                threshold = effects.Bloom.Threshold,
                sigma = effects.Bloom.Sigma,
                strength = effects.Bloom.Strength,
            },
            grain = new { enabled = effects.Grain.Enabled, amount = effects.Grain.Amount, seed = effects.Grain.Seed },
            outputPath = Path.GetFullPath(outputPath),
        });
        await using var run = gateway.CreateRun();
        var result = await run.InvokeAsync(
            new WorkflowActionInvocationRequest(ActionId, arguments),
            progress is null ? null : new PercentProgress(progress),
            cancellationToken).ConfigureAwait(false);
        if (result.Status != WorkflowActionInvocationStatus.Succeeded || result.Output is null)
        {
            throw new InvalidOperationException(result.Failure?.Message ?? "ImageLab Workflow Action 未成功完成。");
        }
        var artifact = result.Output.Value.GetProperty("artifact");
        var resultPath = artifact.GetProperty("path").GetString() ??
                         throw new InvalidDataException("ImageLab 结果缺少输出路径。");
        if (artifact.GetProperty("lifetime").GetString() != FractalWorkflowFileArtifactContract.PersistentLifetime ||
            artifact.GetProperty("producerPluginId").GetString() != "myavalonia.plugin.image.lab" ||
            artifact.GetProperty("contract").GetString() != FractalWorkflowFileArtifactContract.Name ||
            artifact.GetProperty("version").GetInt32() != FractalWorkflowFileArtifactContract.Version ||
            artifact.GetProperty("mediaType").GetString() != FractalWorkflowFileArtifactContract.PngMediaType ||
            !string.Equals(Path.GetFullPath(resultPath), Path.GetFullPath(outputPath), PathComparison))
        {
            throw new InvalidDataException("ImageLab 结果 Artifact 身份无效。");
        }
        var byteLength = artifact.GetProperty("byteLength").GetInt64();
        var sha256 = artifact.GetProperty("sha256").GetString() ?? string.Empty;
        if (byteLength <= 0 || sha256.Length != 64 ||
            sha256.Any(character => !Uri.IsHexDigit(character) || char.IsLower(character)))
        {
            throw new InvalidDataException("ImageLab 结果 Artifact 摘要无效。");
        }
        return new ImageLabExportResult(
            Path.GetFullPath(resultPath),
            byteLength,
            sha256);
    }

    private sealed class PercentProgress(IProgress<int> target) : IProgress<WorkflowActionProgress>
    {
        public void Report(WorkflowActionProgress value)
        {
            if (value.Percent is { } percent)
            {
                target.Report(percent);
            }
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}

internal static class ArtifactJson
{
    internal static object ToObject(WorkflowFileArtifact artifact) => new
    {
        contract = artifact.Contract,
        version = artifact.Version,
        producerPluginId = artifact.ProducerPluginId,
        producerOperationId = artifact.ProducerOperationId.ToString("D"),
        lifetime = artifact.Lifetime,
        path = artifact.Path,
        mediaType = artifact.MediaType,
        byteLength = artifact.ByteLength,
        sha256 = artifact.Sha256,
    };
}

internal sealed class WorkflowRecipeDialog(IPluginWindowInteraction interaction) : IWorkflowRecipeDialog
{
    public Task<string?> PickSavePathAsync(string suggestedName, CancellationToken cancellationToken) =>
        interaction.PickSaveFileAsync(new FilePickerSaveOptions
        {
            Title = "导出 Fractal Workflow 配方",
            SuggestedFileName = suggestedName,
            FileTypeChoices =
            [
                new FilePickerFileType("Fractal Workflow 配方")
                {
                    Patterns = ["*.fractal-workflow.json"]
                }
            ]
        }, cancellationToken);
}

internal sealed class ImageLabExportDialog(IPluginWindowInteraction interaction) : IImageLabExportDialog
{
    public Task<string?> PickOutputPathAsync(string suggestedName, CancellationToken cancellationToken) =>
        interaction.PickSaveFileAsync(new FilePickerSaveOptions
        {
            Title = "经 ImageLab 导出 PNG",
            SuggestedFileName = suggestedName,
            FileTypeChoices = [new FilePickerFileType("PNG 图片") { Patterns = ["*.png"] }]
        }, cancellationToken);
}

internal sealed class FractalArtifactCleanupLifecycle : IPluginLifecycle
{
    public Task InitializeAsync(CancellationToken cancellationToken) =>
        FractalWorkflowArtifactStore.CleanupExpiredFilesAsync(cancellationToken);

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
