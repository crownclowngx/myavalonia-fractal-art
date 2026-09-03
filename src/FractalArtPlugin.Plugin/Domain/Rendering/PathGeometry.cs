namespace FractalArtPlugin.Domain.Rendering;

/// <summary>归一化方形逻辑画板上的二维点；实际输出会把该画板等比居中，避免非方画布扭曲分叉角度。</summary>
public readonly record struct PathPoint(double X, double Y);

/// <summary>
/// 保留递归层级的矢量线段。Level 用于逐层颜色和线宽求值，不能在生成阶段丢失；
/// 后续 SVG 导出可以直接消费同一结构，而不需要从位图反推轮廓。
/// </summary>
public readonly record struct PathSegment(PathPoint Start, PathPoint End, int Level);

/// <summary>
/// 与 UI、Avalonia 和像素格式无关的路径几何。构造时复制输入，防止调用方在生成后修改数组，
/// 使预览、导出和未来的矢量导出都能共享同一份稳定语义。
/// </summary>
public sealed class PathGeometry
{
    public PathGeometry(IEnumerable<PathSegment> segments, int maximumLevel)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var snapshot = segments.ToArray();
        if (snapshot.Length == 0 || maximumLevel < 0 ||
            snapshot.Any(segment => segment.Level < 0 || segment.Level > maximumLevel ||
                !double.IsFinite(segment.Start.X) || !double.IsFinite(segment.Start.Y) ||
                !double.IsFinite(segment.End.X) || !double.IsFinite(segment.End.Y)))
        {
            throw new ArgumentException("路径必须包含有限坐标的线段，且层级不能越界。", nameof(segments));
        }

        Segments = Array.AsReadOnly(snapshot);
        MaximumLevel = maximumLevel;
    }

    public IReadOnlyList<PathSegment> Segments { get; }
    public int MaximumLevel { get; }
}
