namespace FractalArtPlugin.Domain.Fractals.Attractor;

/// <summary>
/// 单步公式策略只描述数学递推。采样预算、Seed、分块、取消和数组所有权由外层生成器统一治理，
/// 因而新增公式不会复制并发与生命周期代码。
/// </summary>
internal interface IAttractorFormulaKernel
{
    AttractorFormula Formula { get; }
    (double X, double Y) Step(StrangeAttractorDefinition definition, double x, double y);
}

internal sealed class CliffordAttractorKernel : IAttractorFormulaKernel
{
    public AttractorFormula Formula => AttractorFormula.Clifford;

    public (double X, double Y) Step(StrangeAttractorDefinition definition, double x, double y) =>
        (Math.Sin(definition.A * y) + definition.C * Math.Cos(definition.A * x),
         Math.Sin(definition.B * x) + definition.D * Math.Cos(definition.B * y));
}

internal sealed class DeJongAttractorKernel : IAttractorFormulaKernel
{
    public AttractorFormula Formula => AttractorFormula.DeJong;

    public (double X, double Y) Step(StrangeAttractorDefinition definition, double x, double y) =>
        (Math.Sin(definition.A * y) - Math.Cos(definition.B * x),
         Math.Sin(definition.C * x) - Math.Cos(definition.D * y));
}

/// <summary>
/// 采用固定 32 条逻辑轨道生成点云。逻辑轨道数量和数组切片与实际线程数无关；调度器只决定何时执行
/// 某个切片，因此 1/2/4/8 路并发得到逐点相同的结果。每条轨道独立预热，避免串行轨道难以安全分块。
/// </summary>
internal sealed class StrangeAttractorPointGenerator : IAttractorPointCloudGenerator
{
    private const int LogicalOrbitCount = 32;
    private readonly IReadOnlyDictionary<AttractorFormula, IAttractorFormulaKernel> _kernels;

    public StrangeAttractorPointGenerator(IEnumerable<IAttractorFormulaKernel> kernels)
    {
        try
        {
            _kernels = kernels.ToDictionary(kernel => kernel.Formula);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("每种吸引子公式必须且只能登记一个公式策略。", exception);
        }
    }

    public async Task<PointCloud> GenerateAsync(
        StrangeAttractorDefinition definition,
        long seed,
        RenderContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(context);
        if (!_kernels.TryGetValue(definition.Formula, out var kernel))
        {
            throw new NotSupportedException($"没有登记吸引子公式 {definition.Formula}。");
        }

        var sampleCount = Math.Min(definition.SampleCount, context.PointSampleBudget);
        var orbitCount = Math.Min(LogicalOrbitCount, sampleCount);
        var points = new PointSample[sampleCount];
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, context.MaxDegreeOfParallelism),
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(Enumerable.Range(0, orbitCount), options, (orbit, token) =>
        {
            var start = sampleCount * orbit / orbitCount;
            var end = sampleCount * (orbit + 1) / orbitCount;
            var state = Mix(unchecked((ulong)seed) ^ ((ulong)definition.Formula << 56) ^ (ulong)orbit);
            var x = ToSignedUnit(Mix(state));
            var y = ToSignedUnit(Mix(state ^ 0x9E3779B97F4A7C15UL));

            for (var iteration = 0; iteration < definition.BurnInIterations; iteration++)
            {
                if ((iteration & 255) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }

                (x, y) = kernel.Step(definition, x, y);
            }

            for (var index = start; index < end; index++)
            {
                if ((index & 1023) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }

                (x, y) = kernel.Step(definition, x, y);
                if (!double.IsFinite(x) || !double.IsFinite(y))
                {
                    throw new InvalidDataException("吸引子公式产生了非有限坐标。");
                }

                points[index] = new PointSample((float)x, (float)y);
            }

            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        return PointCloud.FromOwned(points);
    }

    private static ulong Mix(ulong value)
    {
        value += 0x9E3779B97F4A7C15UL;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    private static double ToSignedUnit(ulong value) =>
        ((value >> 11) * (1d / (1UL << 53))) * 2d - 1d;
}
