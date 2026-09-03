using FractalArtPlugin.Domain.Artwork;
using FractalArtPlugin.Domain.Rendering;

namespace FractalArtPlugin.Domain.Fractals.Mandelbrot;

internal interface IMandelbrotKernel
{
    bool CanHandle(RenderContext context);
    ScalarField Generate(MandelbrotDefinition definition, RenderContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Mandelbrot 标量场入口只验证调度上下文并选择数值内核。公式策略与数值策略均不进入 Document，
/// 后续增加专用扰动内核时只需扩展该窄集合。
/// </summary>
internal sealed class MandelbrotFieldGenerator : IMandelbrotFieldGenerator
{
    private readonly IReadOnlyList<IMandelbrotKernel> _kernels;

    public MandelbrotFieldGenerator() : this([new DoubleMandelbrotKernel(), new ArbitraryMandelbrotKernel()])
    {
    }

    internal MandelbrotFieldGenerator(IEnumerable<IMandelbrotKernel> kernels)
    {
        _kernels = kernels?.ToArray() ?? throw new ArgumentNullException(nameof(kernels));
        if (_kernels.Count == 0)
        {
            throw new ArgumentException("至少需要一个 Mandelbrot 数值内核。", nameof(kernels));
        }
    }

    public Task<ScalarField> GenerateAsync(
        MandelbrotDefinition definition,
        RenderContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var kernel = _kernels.FirstOrDefault(candidate => candidate.CanHandle(context)) ??
            throw new InvalidOperationException("没有 Mandelbrot 内核能够处理当前渲染上下文。");
        return Task.Run(() => kernel.Generate(definition, context, cancellationToken), cancellationToken);
    }
}
