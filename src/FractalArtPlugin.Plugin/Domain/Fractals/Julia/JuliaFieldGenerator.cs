using FractalArtPlugin.Domain.Artwork;
using FractalArtPlugin.Domain.Rendering;

namespace FractalArtPlugin.Domain.Fractals.Julia;

/// <summary>
/// 只负责验证上下文和选择窄内核策略。新增数值策略通过实现 <see cref="IJuliaKernel"/> 扩展，
/// 不需要把分支继续堆进像素循环，也不会让具体内核读取 UI 控件。
/// </summary>
internal sealed class JuliaFieldGenerator : IJuliaFieldGenerator
{
    private readonly IReadOnlyList<IJuliaKernel> _kernels;

    public JuliaFieldGenerator() : this([new PerturbationJuliaKernel(), new DoubleJuliaKernel(), new ArbitraryJuliaKernel()])
    {
    }

    internal JuliaFieldGenerator(IEnumerable<IJuliaKernel> kernels)
    {
        _kernels = kernels?.ToArray() ?? throw new ArgumentNullException(nameof(kernels));
        if (_kernels.Count == 0)
        {
            throw new ArgumentException("至少需要一个 Julia 数值内核。", nameof(kernels));
        }
    }

    public Task<ScalarField> GenerateAsync(
        JuliaDefinition definition,
        RenderContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (context.RendererVersion != RenderContext.CurrentRendererVersion)
        {
            throw new NotSupportedException($"不支持渲染器版本 {context.RendererVersion}。");
        }

        ValidateContext(context);
        var kernel = _kernels.FirstOrDefault(candidate => candidate.CanHandle(context)) ??
            throw new InvalidOperationException("没有内核能够处理当前渲染上下文。");
        return Task.Run(() => kernel.Generate(definition, context, cancellationToken), cancellationToken);
    }

    private static void ValidateContext(RenderContext context)
    {
        if (context.Width <= 0 || context.Height <= 0 || context.EffectivePrecisionDigits <= 0 ||
            context.ConfiguredPrecisionDigits < context.EffectivePrecisionDigits ||
            context.MaxDegreeOfParallelism <= 0 || context.ChunkHeight <= 0 ||
            context.CancellationCheckInterval <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(context), "渲染尺寸、精度或调度预算非法。");
        }
    }
}
