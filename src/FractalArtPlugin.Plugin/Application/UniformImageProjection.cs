namespace FractalArtPlugin.Application;

/// <summary>
/// 把 Uniform 拉伸后的真实图片矩形与点击换算封装为无 UI 值对象。View 只提供控件和位图尺寸，测试无需
/// 启动 Avalonia 即可锁定宽屏、竖屏和信箱边行为，避免把点击留白错误地解释为作品坐标。
/// </summary>
internal readonly record struct UniformImageProjection(double X, double Y, double Width, double Height)
{
    public static UniformImageProjection? Create(
        double availableWidth,
        double availableHeight,
        double imageWidth,
        double imageHeight)
    {
        if (!double.IsFinite(availableWidth) || !double.IsFinite(availableHeight) ||
            !double.IsFinite(imageWidth) || !double.IsFinite(imageHeight) ||
            availableWidth <= 0 || availableHeight <= 0 || imageWidth <= 0 || imageHeight <= 0)
        {
            return null;
        }

        var scale = Math.Min(availableWidth / imageWidth, availableHeight / imageHeight);
        var width = imageWidth * scale;
        var height = imageHeight * scale;
        return new UniformImageProjection(
            (availableWidth - width) / 2,
            (availableHeight - height) / 2,
            width,
            height);
    }

    public MathLensSelection? TryNormalize(double x, double y)
    {
        if (x < X || x > X + Width || y < Y || y > Y + Height)
        {
            return null;
        }

        return new MathLensSelection((x - X) / Width, (y - Y) / Height);
    }
}
