namespace FractalArtPlugin.Features.Artwork;

/// <summary>
/// 上一张真实预览的暂态呈现变换。该值只存在于 Document 运行期，不属于 ArtworkDefinition，
/// 因此保存、撤销、指纹和导出都不可能把插值画面误当作真实计算结果。
/// </summary>
public readonly record struct TransientPreviewTransform(
    double OffsetX,
    double OffsetY,
    double Scale,
    double OriginX,
    double OriginY)
{
    public static TransientPreviewTransform Identity => new(0d, 0d, 1d, 0d, 0d);
    public bool IsIdentity => OffsetX == 0d && OffsetY == 0d && Scale == 1d;

    public TransientPreviewTransform Pan(double deltaX, double deltaY) =>
        this with { OffsetX = OffsetX + deltaX, OffsetY = OffsetY + deltaY };

    public TransientPreviewTransform Zoom(double factor, double originX, double originY) =>
        this with { Scale = Scale * factor, OriginX = originX, OriginY = originY };
}
