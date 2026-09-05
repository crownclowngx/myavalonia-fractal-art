using System.Text.Json;
using System.Text.Json.Serialization;
using FractalArtPlugin.Application.Workflow;
using MyAvaloniaManagement.PluginSdk;

namespace FractalArtPlugin.Infrastructure.Workflow;

internal static class FractalWorkflowActions
{
    internal static readonly WorkflowActionId RenderId =
        new("myavalonia.plugin.fractal.art.workflow.render-artwork-file");

    internal static readonly WorkflowActionId ReleaseId =
        new("myavalonia.plugin.fractal.art.workflow.release-artifact");

    internal static WorkflowActionDescriptor CreateRenderDescriptor()
    {
        using var input = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "recipePath": { "type": "string", "minLength": 1, "maxLength": 32767 }
              },
              "required": ["recipePath"],
              "additionalProperties": false
            }
            """);
        using var output = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "artifact": {
                  "type": "object",
                  "properties": {
                    "contract": { "type": "string", "enum": ["myavalonia.workflow.file-artifact"] },
                    "version": { "type": "integer", "enum": [1] },
                    "producerPluginId": { "type": "string", "enum": ["myavalonia.plugin.fractal.art"] },
                    "producerOperationId": { "type": "string", "minLength": 36, "maxLength": 36 },
                    "lifetime": { "type": "string", "enum": ["run"] },
                    "path": { "type": "string", "minLength": 1, "maxLength": 32767 },
                    "mediaType": { "type": "string", "enum": ["image/png"] },
                    "byteLength": { "type": "integer", "minimum": 1, "maximum": 268435456 },
                    "sha256": { "type": "string", "minLength": 64, "maxLength": 64 }
                  },
                  "required": ["contract", "version", "producerPluginId", "producerOperationId", "lifetime", "path", "mediaType", "byteLength", "sha256"],
                  "additionalProperties": false
                },
                "image": {
                  "type": "object",
                  "properties": {
                    "width": { "type": "integer", "minimum": 1, "maximum": 4096 },
                    "height": { "type": "integer", "minimum": 1, "maximum": 4096 }
                  },
                  "required": ["width", "height"],
                  "additionalProperties": false
                }
              },
              "required": ["artifact", "image"],
              "additionalProperties": false
            }
            """);
        return new WorkflowActionDescriptor(
            RenderId,
            "渲染 Fractal Workflow 配方",
            "读取 Fractal Workflow 配方，以最终质量渲染并创建需要显式释放的 PNG Artifact。",
            input.RootElement,
            output.RootElement,
            WorkflowActionRiskFlags.ReadsLocalFiles |
            WorkflowActionRiskFlags.WritesLocalFiles |
            WorkflowActionRiskFlags.LongRunning,
            WorkflowActionConfirmationPolicy.OncePerRun);
    }

    internal static WorkflowActionDescriptor CreateReleaseDescriptor()
    {
        using var input = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "artifact": {
                  "type": "object",
                  "properties": {
                    "contract": { "type": "string" },
                    "version": { "type": "integer" },
                    "producerPluginId": { "type": "string" },
                    "producerOperationId": { "type": "string" },
                    "lifetime": { "type": "string" },
                    "path": { "type": "string" },
                    "mediaType": { "type": "string" },
                    "byteLength": { "type": "integer" },
                    "sha256": { "type": "string" }
                  },
                  "required": ["contract", "version", "producerPluginId", "producerOperationId", "lifetime", "path", "mediaType", "byteLength", "sha256"],
                  "additionalProperties": false
                }
              },
              "required": ["artifact"],
              "additionalProperties": false
            }
            """);
        using var output = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "released": { "type": "boolean" },
                "warningCode": { "type": "string", "minLength": 1, "maxLength": 64 }
              },
              "required": ["released"],
              "additionalProperties": false
            }
            """);
        return new WorkflowActionDescriptor(
            ReleaseId,
            "释放 Fractal 临时 Artifact",
            "只删除由 Fractal Render Action 创建并带有有效所有权标记的 run 文件。",
            input.RootElement,
            output.RootElement,
            WorkflowActionRiskFlags.DeletesLocalFiles,
            WorkflowActionConfirmationPolicy.EveryInvocation);
    }
}

/// <summary>纯 Provider 边界：读取配方并渲染，不调用 Gateway 或 ImageLab。</summary>
internal sealed class RenderArtworkFileWorkflowActionHandler(
    IWorkflowRecipeFiles recipeFiles,
    IFractalWorkflowArtifactStore artifactStore) : IWorkflowActionHandler
{
    public async ValueTask<JsonElement> InvokeAsync(
        JsonElement arguments,
        WorkflowActionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var input = arguments.Deserialize<RenderArguments>() ??
                    throw new ArgumentException("Fractal Render 参数无法解析。", nameof(arguments));
        context.Progress.Report(new WorkflowActionProgress("validating", 5, "正在验证 Fractal 配方。"));
        var artwork = await recipeFiles.ReadAsync(input.RecipePath, cancellationToken).ConfigureAwait(false);
        if (artwork.Canvas.Width > 4096 || artwork.Canvas.Height > 4096 ||
            (long)artwork.Canvas.Width * artwork.Canvas.Height > 16_777_216)
        {
            throw new InvalidDataException("Workflow 渲染的单边不能超过 4096，总像素不能超过 16,777,216。");
        }
        context.Progress.Report(new WorkflowActionProgress("rendering", 15, "正在以最终质量渲染。"));
        var artifact = await artifactStore.CreateAsync(
            artwork,
            context.InvocationId,
            FractalWorkflowFileArtifactContract.RunLifetime,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var output = JsonSerializer.SerializeToElement(new
            {
                artifact = ArtifactJson.ToObject(artifact),
                image = new { width = artwork.Canvas.Width, height = artwork.Canvas.Height },
            });
            cancellationToken.ThrowIfCancellationRequested();
            context.Progress.Report(new WorkflowActionProgress("succeeded", 100, "Fractal PNG Artifact 已提交。"));
            cancellationToken.ThrowIfCancellationRequested();
            return output;
        }
        catch
        {
            // 创建结束不等于调用提交；迟到取消和序列化失败仍由 Provider 回收当前产物。
            await WorkflowBatchArtifacts.RollbackAsync(artifactStore, artifact).ConfigureAwait(false);
            throw;
        }
    }

    private sealed record RenderArguments(
        [property: JsonPropertyName("recipePath")] string RecipePath);
}

/// <summary>只释放 Fractal 自有 run Artifact；普通删除失败降级为可由 TTL 恢复的警告。</summary>
internal sealed class ReleaseArtifactWorkflowActionHandler(
    IFractalWorkflowArtifactStore artifactStore) : IWorkflowActionHandler
{
    public async ValueTask<JsonElement> InvokeAsync(
        JsonElement arguments,
        WorkflowActionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var input = arguments.Deserialize<ReleaseArguments>() ??
                    throw new ArgumentException("Fractal Release 参数无法解析。", nameof(arguments));
        var result = await artifactStore.ReleaseAsync(
            input.Artifact.ToArtifact(),
            allowTransient: false,
            cancellationToken).ConfigureAwait(false);
        return result.WarningCode is null
            ? JsonSerializer.SerializeToElement(new
            {
                released = result.Released,
            })
            : JsonSerializer.SerializeToElement(new
            {
                released = result.Released,
                warningCode = result.WarningCode,
            });
    }

    private sealed record ReleaseArguments(
        [property: JsonPropertyName("artifact")] ArtifactArguments Artifact);

    private sealed record ArtifactArguments(
        [property: JsonPropertyName("contract")] string Contract,
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("producerPluginId")] string ProducerPluginId,
        [property: JsonPropertyName("producerOperationId")] Guid ProducerOperationId,
        [property: JsonPropertyName("lifetime")] string Lifetime,
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("mediaType")] string MediaType,
        [property: JsonPropertyName("byteLength")] long ByteLength,
        [property: JsonPropertyName("sha256")] string Sha256)
    {
        internal WorkflowFileArtifact ToArtifact() => new(
            Contract, Version, ProducerPluginId, ProducerOperationId, Lifetime,
            Path, MediaType, ByteLength, Sha256);
    }
}
