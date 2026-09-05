namespace FractalArtPlugin.Application;

public sealed record ArtworkExportRequest(int Width, int Height, bool TransparentBackground);

/// <summary>
/// 已通过结构、资源预算和缺失能力检查的不可变导出计划。计划捕获作品快照，确保用户选择文件期间继续编辑
/// 不会让最终 PNG 混入另一个修订；导出器只消费计划，不再承担 UI 会话状态或参数修正职责。
/// </summary>
public sealed record ArtworkExportPlan(
    ArtworkDefinition Artwork,
    RenderContext Context,
    ArtworkExportRequest Request);

public interface IArtworkExportPlanner
{
    ArtworkExportPlan Create(ArtworkDefinition artwork, ArtworkExportRequest request);
}

internal sealed class ArtworkExportPlanner(
    IArtworkValidator validator,
    IArtworkRenderabilityValidator renderability) : IArtworkExportPlanner
{
    public ArtworkExportPlan Create(ArtworkDefinition artwork, ArtworkExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(artwork);
        ArgumentNullException.ThrowIfNull(request);

        // 输出尺寸和透明背景只存在于这份临时快照中。图层变换本来就按画布百分比定义，因此无需复制一条
        // “高分辨率渲染”旁路；现有验证器也会在这里统一执行 64M、Bloom、吸引子和多层工作量预算。
        var background = request.TransparentBackground
            ? artwork.Canvas.Background with { Alpha = 0 }
            : artwork.Canvas.Background;
        var outputArtwork = artwork with
        {
            Canvas = artwork.Canvas with
            {
                Width = request.Width,
                Height = request.Height,
                Background = background
            }
        };

        validator.Validate(outputArtwork);
        renderability.EnsureRenderable(outputArtwork);
        return new ArtworkExportPlan(outputArtwork, RenderContext.ForExport(outputArtwork), request);
    }
}
