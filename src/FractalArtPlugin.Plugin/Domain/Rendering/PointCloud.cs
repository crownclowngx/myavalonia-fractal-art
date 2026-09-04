namespace FractalArtPlugin.Domain.Rendering;

/// <summary>点云中的一个二维样本。坐标保留公式空间语义，栅格化时才执行等比取景。</summary>
public readonly record struct PointSample(float X, float Y);

/// <summary>
/// 与 UI 和像素尺寸无关的不可变点云。构造时复制外部缓冲；只有同程序集数值内核可以通过
/// <see cref="FromOwned"/> 零复制移交刚分配且尚未泄漏的数组。
/// </summary>
public sealed class PointCloud
{
    private readonly PointSample[] _points;
    private readonly ReadOnlyBuffer<PointSample> _pointsView;

    public PointCloud(IEnumerable<PointSample> points)
        : this(points?.ToArray() ?? throw new ArgumentNullException(nameof(points)), takeOwnership: true)
    {
    }

    private PointCloud(PointSample[] points, bool takeOwnership)
    {
        if (points.Length == 0 || points.Any(point => !float.IsFinite(point.X) || !float.IsFinite(point.Y)))
        {
            throw new ArgumentException("点云必须包含至少一个有限坐标点。", nameof(points));
        }

        _points = takeOwnership ? points : (PointSample[])points.Clone();
        _pointsView = new ReadOnlyBuffer<PointSample>(_points);
        MinimumX = _points.Min(point => point.X);
        MaximumX = _points.Max(point => point.X);
        MinimumY = _points.Min(point => point.Y);
        MaximumY = _points.Max(point => point.Y);
    }

    public ReadOnlyBuffer<PointSample> Points => _pointsView;
    public float MinimumX { get; }
    public float MaximumX { get; }
    public float MinimumY { get; }
    public float MaximumY { get; }
    internal long EstimatedByteSize => checked((long)_points.Length * sizeof(float) * 2 + sizeof(float) * 4);

    internal static PointCloud FromOwned(PointSample[] points) => new(points, takeOwnership: true);
}
