namespace FractalArtPlugin.Domain.Rendering;

/// <summary>
/// 对内部数组的只读视图。它保留长度、索引和枚举能力，但不暴露可写数组，
/// 让数值内核可以零复制移交所有权，同时保证进入节点缓存后的内容不会被调用方篡改。
/// </summary>
public sealed class ReadOnlyBuffer<T> : IReadOnlyList<T>, IEquatable<ReadOnlyBuffer<T>>
    where T : IEquatable<T>
{
    private readonly T[] _items;

    internal ReadOnlyBuffer(T[] items) => _items = items;

    public int Length => _items.Length;
    public int Count => _items.Length;
    public T this[int index] => _items[index];
    public ReadOnlySpan<T> Span => _items;
    public T[] ToArray() => (T[])_items.Clone();
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _items.GetEnumerator();

    public bool Equals(ReadOnlyBuffer<T>? other) => other is not null && _items.AsSpan().SequenceEqual(other._items);
    public override bool Equals(object? obj) => obj is ReadOnlyBuffer<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in _items)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }
}

/// <summary>运行时渲染诊断；仅用于状态栏、测试和基准，不是作品状态。</summary>
public sealed record RenderDiagnostics(
    string Kernel,
    int ConfiguredPrecisionDigits,
    int EffectivePrecisionDigits,
    int MaxDegreeOfParallelism,
    int PrecisionFallbackPixels,
    int PerturbationGlitchPixels);

/// <summary>归一化迭代标量场；Escaped 单独保存，避免把内部点与低迭代值混淆。</summary>
public sealed class ScalarField
{
    private readonly float[] _values;
    private readonly bool[] _escaped;
    private readonly ReadOnlyBuffer<float> _valuesView;
    private readonly ReadOnlyBuffer<bool> _escapedView;

    public ScalarField(
        int width,
        int height,
        float[] values,
        bool[] escaped,
        RenderDiagnostics? diagnostics = null)
    {
        if (width <= 0 || height <= 0 || values.Length != width * height || escaped.Length != values.Length)
        {
            throw new ArgumentException("标量场尺寸与数据长度不一致。");
        }

        Width = width;
        Height = height;
        _values = (float[])values.Clone();
        _escaped = (bool[])escaped.Clone();
        _valuesView = new ReadOnlyBuffer<float>(_values);
        _escapedView = new ReadOnlyBuffer<bool>(_escaped);
        Diagnostics = diagnostics ?? new RenderDiagnostics("unknown", 0, 0, 1, 0, 0);
    }

    private ScalarField(
        int width,
        int height,
        float[] values,
        bool[] escaped,
        RenderDiagnostics? diagnostics,
        bool takeOwnership)
    {
        Width = width;
        Height = height;
        _values = takeOwnership ? values : (float[])values.Clone();
        _escaped = takeOwnership ? escaped : (bool[])escaped.Clone();
        _valuesView = new ReadOnlyBuffer<float>(_values);
        _escapedView = new ReadOnlyBuffer<bool>(_escaped);
        Diagnostics = diagnostics ?? new RenderDiagnostics("unknown", 0, 0, 1, 0, 0);
    }

    public int Width { get; }
    public int Height { get; }
    public ReadOnlyBuffer<float> Values => _valuesView;
    public ReadOnlyBuffer<bool> Escaped => _escapedView;
    public RenderDiagnostics Diagnostics { get; }
    internal long EstimatedByteSize => checked((long)_values.Length * sizeof(float) + _escaped.Length);

    /// <summary>
    /// 数值内核刚刚分配的数组尚未泄漏给外部时可以直接移交所有权，避免为不可变缓存额外复制整幅标量场。
    /// 该入口保持 internal，跨程序集调用方仍必须经过防御性复制的公共构造函数。
    /// </summary>
    internal static ScalarField FromOwned(
        int width,
        int height,
        float[] values,
        bool[] escaped,
        RenderDiagnostics? diagnostics = null)
    {
        if (width <= 0 || height <= 0 || values.Length != width * height || escaped.Length != values.Length)
        {
            throw new ArgumentException("标量场尺寸与数据长度不一致。");
        }

        return new ScalarField(width, height, values, escaped, diagnostics, true);
    }
}

/// <summary>与 UI 框架无关的 RGBA8888 图像面；导出与预览共享这一真实渲染结果。</summary>
public sealed class ImageSurface
{
    private readonly byte[] _pixels;
    private readonly ReadOnlyBuffer<byte> _pixelView;

    public ImageSurface(int width, int height, byte[] pixels, RenderDiagnostics? diagnostics = null)
    {
        if (width <= 0 || height <= 0 || pixels.Length != checked(width * height * 4))
        {
            throw new ArgumentException("RGBA 图像尺寸与像素长度不一致。");
        }

        Width = width;
        Height = height;
        _pixels = (byte[])pixels.Clone();
        _pixelView = new ReadOnlyBuffer<byte>(_pixels);
        Diagnostics = diagnostics;
    }

    private ImageSurface(int width, int height, byte[] pixels, RenderDiagnostics? diagnostics, bool takeOwnership)
    {
        Width = width;
        Height = height;
        _pixels = takeOwnership ? pixels : (byte[])pixels.Clone();
        _pixelView = new ReadOnlyBuffer<byte>(_pixels);
        Diagnostics = diagnostics;
    }

    public int Width { get; }
    public int Height { get; }
    public ReadOnlyBuffer<byte> Pixels => _pixelView;
    public RenderDiagnostics? Diagnostics { get; }
    internal long EstimatedByteSize => _pixels.LongLength;

    internal static ImageSurface FromOwned(
        int width,
        int height,
        byte[] pixels,
        RenderDiagnostics? diagnostics = null)
    {
        if (width <= 0 || height <= 0 || pixels.Length != checked(width * height * 4))
        {
            throw new ArgumentException("RGBA 图像尺寸与像素长度不一致。");
        }

        return new ImageSurface(width, height, pixels, diagnostics, true);
    }
}

/// <summary>
/// 只读 8 位遮罩；0 表示完全排除，255 表示完全包含。首版只建立稳定数据契约，
/// 具体遮罩节点留给 G0008，避免在效果层私自约定另一种尺寸或透明度语义。
/// </summary>
public sealed class Mask
{
    private readonly byte[] _values;
    private readonly ReadOnlyBuffer<byte> _valueView;

    public Mask(int width, int height, byte[] values)
    {
        if (width <= 0 || height <= 0 || values.Length != checked(width * height))
        {
            throw new ArgumentException("遮罩尺寸与数据长度不一致。", nameof(values));
        }

        Width = width;
        Height = height;
        _values = (byte[])values.Clone();
        _valueView = new ReadOnlyBuffer<byte>(_values);
    }

    public int Width { get; }
    public int Height { get; }
    public ReadOnlyBuffer<byte> Values => _valueView;
    internal long EstimatedByteSize => _values.LongLength;
}
