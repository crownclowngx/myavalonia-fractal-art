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
    internal const int LogicalOrbitCount = 32;
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
            var initial = CreateInitialState(seed, definition.Formula, orbit);
            var x = initial.X;
            var y = initial.Y;

            (x, y) = AdvanceBurnIn(definition, kernel, x, y, token);

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

    /// <summary>
    /// 为指定逻辑轨道派生稳定初值。生成器与数学透镜必须调用同一入口；否则透镜即使公式相同，展示的
    /// “第 0 条轨道”也可能因另一套随机派生而与作品点云无关。
    /// </summary>
    internal static (double X, double Y) CreateInitialState(long seed, AttractorFormula formula, int orbit)
    {
        if (orbit is < 0 or >= LogicalOrbitCount)
        {
            throw new ArgumentOutOfRangeException(nameof(orbit));
        }

        var state = Mix(unchecked((ulong)seed) ^ ((ulong)formula << 56) ^ (ulong)orbit);
        return (ToSignedUnit(Mix(state)), ToSignedUnit(Mix(state ^ 0x9E3779B97F4A7C15UL)));
    }

    /// <summary>
    /// 执行生产点云使用的完整预热。可选轨迹收集器只由数学透镜传入；正常生成不会分配预热数组，二者
    /// 却共享相同的取消间隔、公式步进和有限值检查。
    /// </summary>
    internal static (double X, double Y) AdvanceBurnIn(
        StrangeAttractorDefinition definition,
        IAttractorFormulaKernel kernel,
        double x,
        double y,
        CancellationToken cancellationToken,
        ICollection<(double X, double Y)>? trace = null)
    {
        trace?.Add((x, y));
        for (var iteration = 0; iteration < definition.BurnInIterations; iteration++)
        {
            if ((iteration & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            (x, y) = kernel.Step(definition, x, y);
            if (!double.IsFinite(x) || !double.IsFinite(y))
            {
                throw new InvalidDataException("吸引子公式产生了非有限坐标。");
            }

            trace?.Add((x, y));
        }

        return (x, y);
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
