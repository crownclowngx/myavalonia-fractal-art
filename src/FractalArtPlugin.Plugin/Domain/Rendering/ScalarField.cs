namespace FractalArtPlugin.Domain.Rendering;

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
        Values = values;
        Escaped = escaped;
        Diagnostics = diagnostics ?? new RenderDiagnostics("unknown", 0, 0, 1, 0, 0);
    }

    public int Width { get; }
    public int Height { get; }
    public float[] Values { get; }
    public bool[] Escaped { get; }
    public RenderDiagnostics Diagnostics { get; }
}

/// <summary>与 UI 框架无关的 RGBA8888 图像面；导出与预览共享这一真实渲染结果。</summary>
public sealed class RgbaImage
{
    public RgbaImage(int width, int height, byte[] pixels, RenderDiagnostics? diagnostics = null)
    {
        if (width <= 0 || height <= 0 || pixels.Length != checked(width * height * 4))
        {
            throw new ArgumentException("RGBA 图像尺寸与像素长度不一致。");
        }

        Width = width;
        Height = height;
        Pixels = pixels;
        Diagnostics = diagnostics;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    public RenderDiagnostics? Diagnostics { get; }
}
