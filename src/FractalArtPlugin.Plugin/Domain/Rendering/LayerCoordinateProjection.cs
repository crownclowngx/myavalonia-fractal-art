using FractalArtPlugin.Domain.Artwork;

namespace FractalArtPlugin.Domain.Rendering;

/// <summary>
/// 图层坐标变换的唯一数学实现。栅格器使用逆变换采样，数学透镜使用同一逆变换定位被点击的原始像素，
/// 并使用正变换把生成器标注放回合成画布。集中这组公式可以避免预览与解释层在旋转方向、锚点或百分比
/// 位移上逐渐产生两套语义。
/// </summary>
internal static class LayerCoordinateProjection
{
    public static (double X, double Y) InverseMap(
        double x,
        double y,
        int width,
        int height,
        LayerTransformDefinition transform)
    {
        var anchorX = width * transform.AnchorXPercent / 100d;
        var anchorY = height * transform.AnchorYPercent / 100d;
        var translatedX = x - anchorX - width * transform.PositionXPercent / 100d;
        var translatedY = y - anchorY - height * transform.PositionYPercent / 100d;
        var radians = -transform.RotationDegrees * Math.PI / 180d;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var scale = transform.ScalePercent / 100d;
        return (
            (translatedX * cosine - translatedY * sine) / scale + anchorX,
            (translatedX * sine + translatedY * cosine) / scale + anchorY);
    }

    public static (double X, double Y) ForwardMap(
        double x,
        double y,
        int width,
        int height,
        LayerTransformDefinition transform)
    {
        var anchorX = width * transform.AnchorXPercent / 100d;
        var anchorY = height * transform.AnchorYPercent / 100d;
        var translatedX = (x - anchorX) * transform.ScalePercent / 100d;
        var translatedY = (y - anchorY) * transform.ScalePercent / 100d;
        var radians = transform.RotationDegrees * Math.PI / 180d;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        return (
            translatedX * cosine - translatedY * sine + anchorX + width * transform.PositionXPercent / 100d,
            translatedX * sine + translatedY * cosine + anchorY + height * transform.PositionYPercent / 100d);
    }
}
