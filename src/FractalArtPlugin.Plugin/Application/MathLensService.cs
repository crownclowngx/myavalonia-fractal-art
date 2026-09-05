using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;

namespace FractalArtPlugin.Application;

public enum MathLensKind
{
    Information,
    EscapeOrbit,
    PathConstruction,
    AttractorFormation
}

public readonly record struct MathLensSelection(double X, double Y)
{
    public static MathLensSelection Center { get; } = new(0.5, 0.5);
}

public readonly record struct MathLensPoint(double X, double Y);
public readonly record struct MathLensSegment(MathLensPoint Start, MathLensPoint End);

/// <summary>
/// 一个只读展示帧。坐标统一使用作品画布的 0–1 归一化空间，Avalonia 只负责把它们投影到当前控件；
/// Visible 数量允许多个帧共享同一不可变集合，避免吸引子播放为每帧复制数万个点。
/// </summary>
public sealed record MathLensFrame(
    string Title,
    string Annotation,
    int SourceStep,
    int SourceMaximum,
    IReadOnlyList<MathLensSegment> Segments,
    int VisibleSegmentCount,
    IReadOnlyList<MathLensPoint> Points,
    int VisiblePointCount,
    MathLensPoint? Marker = null);

/// <summary>一次分析的完整不可变结果；它只属于 Document 会话，绝不进入作品快照或创作图缓存键。</summary>
public sealed record MathLensAnalysis(
    string LayerId,
    MathLensKind Kind,
    string Title,
    string Formula,
    string Explanation,
    IReadOnlyList<MathLensFrame> Frames)
{
    public static MathLensAnalysis Information(string layerId, string title, string explanation) => new(
        layerId,
        MathLensKind.Information,
        title,
        string.Empty,
        explanation,
        [new MathLensFrame(title, explanation, 0, 0, [], 0, [], 0)]);
}

internal interface IMathLensService
{
    Task<MathLensAnalysis> AnalyzeAsync(
        ArtworkDefinition artwork,
        string selectedLayerId,
        MathLensSelection? selection,
        CancellationToken cancellationToken);
}

internal interface IMathLensProvider
{
    bool Supports(FractalGeneratorKind kind);

    Task<MathLensAnalysis> AnalyzeAsync(
        ArtworkDefinition artwork,
        FractalLayerDefinition layer,
        MathLensSelection? selection,
        CancellationToken cancellationToken);
}

/// <summary>
/// 只按生成器选择窄策略，不认识数值内核、路径格式或 Avalonia。每种生成器必须且只能由一个 Provider
/// 处理，使新增透镜时遵守开闭原则，同时避免在 Document 中堆叠类型分支。
/// </summary>
internal sealed class MathLensService : IMathLensService
{
    private readonly IReadOnlyDictionary<FractalGeneratorKind, IMathLensProvider> _providers;

    public MathLensService(IEnumerable<IMathLensProvider> providers)
    {
        var pairs = providers.SelectMany(provider => Enum.GetValues<FractalGeneratorKind>()
            .Where(provider.Supports)
            .Select(kind => (Kind: kind, Provider: provider)));
        try
        {
            _providers = pairs.ToDictionary(pair => pair.Kind, pair => pair.Provider);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("每种生成器必须且只能登记一个数学透镜策略。", exception);
        }
    }

    public Task<MathLensAnalysis> AnalyzeAsync(
        ArtworkDefinition artwork,
        string selectedLayerId,
        MathLensSelection? selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artwork);
        cancellationToken.ThrowIfCancellationRequested();
        var selected = ArtworkLayerTree.Find(artwork.Layers, selectedLayerId);
        if (selected is not FractalLayerDefinition layer)
        {
            return Task.FromResult(MathLensAnalysis.Information(
                selectedLayerId,
                "请选择分形层",
                "数学透镜只解释当前选中的分形层；分组和不可用占位没有单一生成公式。"));
        }

        if (!_providers.TryGetValue(layer.GeneratorKind, out var provider))
        {
            return Task.FromResult(MathLensAnalysis.Information(
                layer.Id,
                "暂不支持此生成器",
                $"{layer.Name} 当前没有可用的数学透镜。"));
        }

        return provider.AnalyzeAsync(artwork, layer, selection, cancellationToken);
    }
}

internal static class MathLensProjection
{
    public static (MathLensSelection Local, RenderContext Context)? ResolveSelection(
        ArtworkDefinition artwork,
        FractalLayerDefinition layer,
        MathLensSelection selection)
    {
        var selected = artwork.SelectLayer(layer.Id);
        var frame = RenderContext.ForPreview(selected);
        var context = RenderContext.ForLayer(selected, layer, frame);
        var targetX = selection.X * Math.Max(1, context.Width - 1);
        var targetY = selection.Y * Math.Max(1, context.Height - 1);
        var local = LayerCoordinateProjection.InverseMap(
            targetX, targetY, context.Width, context.Height, layer.Transform);
        var maximumX = Math.Max(1, context.Width - 1);
        var maximumY = Math.Max(1, context.Height - 1);
        if (local.X < 0 || local.X > maximumX || local.Y < 0 || local.Y > maximumY)
        {
            return null;
        }

        return (new MathLensSelection(local.X / maximumX, local.Y / maximumY), context);
    }

    public static MathLensPoint ToCanvas(
        double normalizedX,
        double normalizedY,
        RenderContext context,
        LayerTransformDefinition transform)
    {
        var mapped = LayerCoordinateProjection.ForwardMap(
            normalizedX * Math.Max(1, context.Width - 1),
            normalizedY * Math.Max(1, context.Height - 1),
            context.Width,
            context.Height,
            transform);
        return new MathLensPoint(
            mapped.X / Math.Max(1, context.Width - 1),
            mapped.Y / Math.Max(1, context.Height - 1));
    }

    public static IReadOnlyList<int> SampleIndices(int count, int maximumFrames)
    {
        if (count <= 0 || maximumFrames <= 0)
        {
            return [];
        }

        if (maximumFrames == 1)
        {
            return [0];
        }

        if (count <= maximumFrames)
        {
            return Enumerable.Range(0, count).ToArray();
        }

        var indices = new int[maximumFrames];
        for (var index = 0; index < maximumFrames; index++)
        {
            indices[index] = (int)Math.Round(index * (count - 1d) / (maximumFrames - 1d));
        }

        return indices;
    }
}

internal sealed class EscapeTimeMathLensProvider(IGradientMapper gradientMapper) : IMathLensProvider
{
    private const int MaximumFrames = 240;

    public bool Supports(FractalGeneratorKind kind) =>
        kind is FractalGeneratorKind.Julia or FractalGeneratorKind.Mandelbrot;

    public Task<MathLensAnalysis> AnalyzeAsync(
        ArtworkDefinition artwork,
        FractalLayerDefinition layer,
        MathLensSelection? selection,
        CancellationToken cancellationToken) => Task.Run(() => Analyze(
            artwork, layer, selection ?? MathLensSelection.Center, cancellationToken), cancellationToken);

    private MathLensAnalysis Analyze(
        ArtworkDefinition artwork,
        FractalLayerDefinition layer,
        MathLensSelection selection,
        CancellationToken cancellationToken)
    {
        var resolved = MathLensProjection.ResolveSelection(artwork, layer, selection);
        if (resolved is null)
        {
            return MathLensAnalysis.Information(
                layer.Id,
                "点击位于当前层之外",
                "当前图层经过位移、旋转或缩放后没有覆盖该点，请点击图层实际显示区域。");
        }

        var (local, context) = resolved.Value;
        var pixelX = Math.Clamp((int)Math.Round(local.X * Math.Max(1, context.Width - 1)), 0, context.Width - 1);
        var pixelY = Math.Clamp((int)Math.Round(local.Y * Math.Max(1, context.Height - 1)), 0, context.Height - 1);
        var trace = new List<EscapeOrbitPoint>();
        EscapeOrbitSample sample;
        double viewportCenterX;
        double viewportCenterY;
        double viewportScale;
        string formula;
        string pointLabel;
        int maximumIterations;

        if (layer.GeneratorKind == FractalGeneratorKind.Julia)
        {
            var definition = layer.Julia;
            maximumIterations = definition.MaxIterations;
            viewportCenterX = ArbitraryDecimal.Parse(definition.CenterX).ToDouble();
            viewportCenterY = ArbitraryDecimal.Parse(definition.CenterY).ToDouble();
            viewportScale = ArbitraryDecimal.Parse(definition.Scale).ToDouble();
            formula = "zₙ₊₁ = zₙ² + c；z₀ 为所选像素，c 为作品中的 Julia 常量";
            if (context.NumericPrecision == NumericPrecision.Double)
            {
                var coordinates = ResolveDoubleCoordinates(
                    definition.CenterX, definition.CenterY, definition.Scale, context, pixelX, pixelY);
                sample = EscapeOrbitMath.ComputeDouble(
                    coordinates.Real,
                    coordinates.Imaginary,
                    ArbitraryDecimal.Parse(definition.ConstantReal).ToDouble(),
                    ArbitraryDecimal.Parse(definition.ConstantImaginary).ToDouble(),
                    maximumIterations,
                    context.CancellationCheckInterval,
                    cancellationToken,
                    trace);
                pointLabel = $"z₀ = {FormatComplex(coordinates.Real, coordinates.Imaginary)}";
            }
            else
            {
                sample = TraceFixedJulia(definition, context, pixelX, pixelY, trace, cancellationToken);
                pointLabel = $"所选像素 ({pixelX}, {pixelY}) 使用 {context.EffectivePrecisionDigits} 位权威定点轨迹";
            }
        }
        else
        {
            var definition = layer.Mandelbrot;
            maximumIterations = definition.MaxIterations;
            viewportCenterX = ArbitraryDecimal.Parse(definition.CenterX).ToDouble();
            viewportCenterY = ArbitraryDecimal.Parse(definition.CenterY).ToDouble();
            viewportScale = ArbitraryDecimal.Parse(definition.Scale).ToDouble();
            formula = "zₙ₊₁ = zₙ² + c；z₀ = 0，c 为所选像素";
            if (context.NumericPrecision == NumericPrecision.Double)
            {
                var coordinates = ResolveDoubleCoordinates(
                    definition.CenterX, definition.CenterY, definition.Scale, context, pixelX, pixelY);
                sample = EscapeOrbitMath.ComputeDouble(
                    0,
                    0,
                    coordinates.Real,
                    coordinates.Imaginary,
                    maximumIterations,
                    context.CancellationCheckInterval,
                    cancellationToken,
                    trace);
                pointLabel = $"c = {FormatComplex(coordinates.Real, coordinates.Imaginary)}";
            }
            else
            {
                sample = TraceFixedMandelbrot(definition, context, pixelX, pixelY, trace, cancellationToken);
                pointLabel = $"所选 c 像素 ({pixelX}, {pixelY}) 使用 {context.EffectivePrecisionDigits} 位权威定点轨迹";
            }
        }

        var scalar = sample.ToScalar(maximumIterations);
        var onePixel = new ScalarField(1, 1, [scalar.Value], [scalar.Escaped]);
        var mapped = gradientMapper.Map(onePixel, layer.Gradient, cancellationToken);
        var rgba = mapped.Pixels.Span;
        var color = new RgbaColor(rgba[0], rgba[1], rgba[2], rgba[3]);
        var projected = ProjectOrbit(trace, viewportCenterX, viewportCenterY, viewportScale, context, layer.Transform);
        var segments = projected.Zip(projected.Skip(1), (start, end) => new MathLensSegment(start, end)).ToArray();
        var marker = MathLensProjection.ToCanvas(local.X, local.Y, context, layer.Transform);
        var frames = new List<MathLensFrame>();
        foreach (var traceIndex in MathLensProjection.SampleIndices(trace.Count, MaximumFrames))
        {
            var current = trace[traceIndex];
            var visibleSegments = Math.Min(traceIndex, segments.Length);
            frames.Add(new MathLensFrame(
                $"轨迹 z{current.Iteration}",
                $"{pointLabel}；当前 z = {FormatComplex(current.Real, current.Imaginary)}",
                current.Iteration,
                maximumIterations,
                segments,
                visibleSegments,
                [],
                0,
                marker));
        }

        var result = scalar.Escaped
            ? $"第 {sample.Iteration} 次迭代后逃逸，归一化值 {scalar.Value:0.0000}，基础颜色 {color.ToHex()}。"
            : $"在 {maximumIterations} 次预算内没有逃逸，使用内部颜色 {color.ToHex()}。";
        return new MathLensAnalysis(
            layer.Id,
            MathLensKind.EscapeOrbit,
            $"{layer.Name} · 单点逃逸轨迹",
            formula,
            VisibilityPrefix(layer) + result + " 画布仍显示完整合成结果；标注解释的是当前层进入混合前的数学来源。",
            new ReadOnlyCollection<MathLensFrame>(frames));
    }

    private static EscapeOrbitSample TraceFixedJulia(
        JuliaDefinition definition,
        RenderContext context,
        int pixelX,
        int pixelY,
        ICollection<EscapeOrbitPoint> trace,
        CancellationToken cancellationToken)
    {
        var fixedPoint = BinaryFixedPoint.ForDecimalDigits(context.EffectivePrecisionDigits);
        var frame = ArbitraryJuliaKernel.FrameCoordinates.Create(definition, context, fixedPoint);
        var sample = EscapeOrbitMath.ComputeFixed(
            fixedPoint,
            frame.Left + pixelX * frame.PixelStep,
            frame.Top + pixelY * frame.PixelStep,
            frame.ConstantReal,
            frame.ConstantImaginary,
            definition.MaxIterations,
            context.CancellationCheckInterval,
            cancellationToken,
            trace);
        if (!sample.NearEscapeBoundary || context.EffectivePrecisionDigits == context.ConfiguredPrecisionDigits)
        {
            return sample;
        }

        trace.Clear();
        fixedPoint = BinaryFixedPoint.ForDecimalDigits(context.ConfiguredPrecisionDigits);
        frame = ArbitraryJuliaKernel.FrameCoordinates.Create(definition, context with
        {
            EffectivePrecisionDigits = context.ConfiguredPrecisionDigits
        }, fixedPoint);
        return EscapeOrbitMath.ComputeFixed(
            fixedPoint,
            frame.Left + pixelX * frame.PixelStep,
            frame.Top + pixelY * frame.PixelStep,
            frame.ConstantReal,
            frame.ConstantImaginary,
            definition.MaxIterations,
            context.CancellationCheckInterval,
            cancellationToken,
            trace);
    }

    private static EscapeOrbitSample TraceFixedMandelbrot(
        MandelbrotDefinition definition,
        RenderContext context,
        int pixelX,
        int pixelY,
        ICollection<EscapeOrbitPoint> trace,
        CancellationToken cancellationToken)
    {
        var fixedPoint = BinaryFixedPoint.ForDecimalDigits(context.EffectivePrecisionDigits);
        var centerX = fixedPoint.Parse(definition.CenterX);
        var centerY = fixedPoint.Parse(definition.CenterY);
        var scale = fixedPoint.Parse(definition.Scale);
        var step = BinaryFixedPoint.DivideRounded(scale, Math.Max(1, context.Height - 1));
        var constantReal = centerX - BinaryFixedPoint.DivideRounded(step * (context.Width - 1), 2) + pixelX * step;
        var constantImaginary = centerY - BinaryFixedPoint.DivideRounded(scale, 2) + pixelY * step;
        return EscapeOrbitMath.ComputeFixed(
            fixedPoint,
            BigInteger.Zero,
            BigInteger.Zero,
            constantReal,
            constantImaginary,
            definition.MaxIterations,
            context.CancellationCheckInterval,
            cancellationToken,
            trace);
    }

    private static (double Real, double Imaginary) ResolveDoubleCoordinates(
        string centerXText,
        string centerYText,
        string scaleText,
        RenderContext context,
        int pixelX,
        int pixelY)
    {
        var centerX = ArbitraryDecimal.Parse(centerXText).ToDouble();
        var centerY = ArbitraryDecimal.Parse(centerYText).ToDouble();
        var scale = ArbitraryDecimal.Parse(scaleText).ToDouble();
        var step = scale / Math.Max(1, context.Height - 1);
        return (
            centerX - (context.Width - 1) * step / 2d + pixelX * step,
            centerY - scale / 2d + pixelY * step);
    }

    private static IReadOnlyList<MathLensPoint> ProjectOrbit(
        IReadOnlyList<EscapeOrbitPoint> trace,
        double centerX,
        double centerY,
        double scale,
        RenderContext context,
        LayerTransformDefinition transform)
    {
        if (!double.IsFinite(scale) || scale <= 0)
        {
            return [];
        }

        var step = scale / Math.Max(1, context.Height - 1);
        var left = centerX - (context.Width - 1) * step / 2d;
        var top = centerY - scale / 2d;
        return trace.Select(point => MathLensProjection.ToCanvas(
            (point.Real - left) / Math.Max(step, double.Epsilon) / Math.Max(1, context.Width - 1),
            (point.Imaginary - top) / Math.Max(step, double.Epsilon) / Math.Max(1, context.Height - 1),
            context,
            transform)).ToArray();
    }

    private static string FormatComplex(double real, double imaginary) =>
        string.Create(CultureInfo.InvariantCulture, $"{real:0.######} {(imaginary < 0 ? '-' : '+')} {Math.Abs(imaginary):0.######}i");

    private static string VisibilityPrefix(FractalLayerDefinition layer) =>
        layer.IsVisible ? string.Empty : "当前层已隐藏；透镜仍按已保存配方解释。";
}

internal sealed class PathMathLensProvider(
    IRecursiveTreePathGenerator treeGenerator,
    ILSystemExpander lSystemExpander,
    ITurtlePathInterpreter turtleInterpreter) : IMathLensProvider
{
    private const int MaximumDrawingBatches = 120;

    public bool Supports(FractalGeneratorKind kind) =>
        kind is FractalGeneratorKind.RecursiveTree or FractalGeneratorKind.LSystem;

    public Task<MathLensAnalysis> AnalyzeAsync(
        ArtworkDefinition artwork,
        FractalLayerDefinition layer,
        MathLensSelection? selection,
        CancellationToken cancellationToken) => Task.Run(
            () => layer.GeneratorKind == FractalGeneratorKind.RecursiveTree
                ? AnalyzeTree(artwork, layer, cancellationToken)
                : AnalyzeLSystem(artwork, layer, cancellationToken),
            cancellationToken);

    private MathLensAnalysis AnalyzeTree(
        ArtworkDefinition artwork,
        FractalLayerDefinition layer,
        CancellationToken cancellationToken)
    {
        var geometry = treeGenerator.Generate(layer.RecursiveTree, layer.Seed, cancellationToken);
        var context = ResolveContext(artwork, layer);
        var segments = ProjectSegments(geometry.Segments, context, layer.Transform);
        var frames = new List<MathLensFrame>();
        for (var level = 0; level <= geometry.MaximumLevel; level++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var visible = geometry.Segments.Count(segment => segment.Level <= level);
            frames.Add(new MathLensFrame(
                $"递归层级 {level}",
                $"显示从根到第 {level} 层的 {visible} 条线段；下一层从上一层枝端继续缩短并分叉。",
                level,
                geometry.MaximumLevel,
                segments,
                visible,
                [],
                0));
        }

        return new MathLensAnalysis(
            layer.Id,
            MathLensKind.PathConstruction,
            $"{layer.Name} · 递归生长",
            "Lₙ₊₁ = Lₙ × 长度衰减；方向 = 父方向 ± 分叉角 + Seed 扰动",
            VisibilityPrefix(layer) + "每一帧直接筛选生产路径携带的 Level，不从位图反推结构。",
            frames.AsReadOnly());
    }

    private MathLensAnalysis AnalyzeLSystem(
        ArtworkDefinition artwork,
        FractalLayerDefinition layer,
        CancellationToken cancellationToken)
    {
        var definition = layer.LSystem;
        var context = ResolveContext(artwork, layer);
        var frames = new List<MathLensFrame>();
        var generations = lSystemExpander.ExpandGenerations(definition, cancellationToken);
        var finalSymbols = generations[^1];
        IReadOnlyList<MathLensSegment> finalSegments = [];
        for (var generation = 0; generation < generations.Count; generation++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentDefinition = definition with { Iterations = generation };
            var symbols = generations[generation];
            var geometry = TryInterpret(currentDefinition, symbols, cancellationToken);
            var segments = geometry is null ? [] : ProjectSegments(geometry.Segments, context, layer.Transform);
            frames.Add(new MathLensFrame(
                $"替换第 {generation} 轮",
                DescribeGeneration(definition, generation, symbols),
                generation,
                definition.Iterations,
                segments,
                segments.Count,
                [],
                0));
            if (generation == definition.Iterations)
            {
                finalSymbols = symbols;
                finalSegments = segments;
            }
        }

        var batchSize = Math.Max(1, (int)Math.Ceiling(finalSymbols.Length / (double)MaximumDrawingBatches));
        var drawn = 0;
        var previousEnd = 0;
        while (previousEnd < finalSymbols.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = Math.Min(finalSymbols.Length, previousEnd + batchSize);
            var batch = finalSymbols.AsSpan(previousEnd, end - previousEnd);
            drawn += batch.Count('F');
            drawn += batch.Count('G');
            var currentSymbol = finalSymbols[end - 1];
            frames.Add(new MathLensFrame(
                $"绘制动作 {end}/{finalSymbols.Length}",
                $"当前符号 {currentSymbol}：{DescribeSymbol(currentSymbol)}；已经产生 {Math.Min(drawn, finalSegments.Count)} 条线段。",
                end,
                finalSymbols.Length,
                finalSegments,
                Math.Min(drawn, finalSegments.Count),
                [],
                0));
            previousEnd = end;
        }

        return new MathLensAnalysis(
            layer.Id,
            MathLensKind.PathConstruction,
            $"{layer.Name} · L-System 替换与绘制",
            "每一轮把符号按产生式替换；F/G 前进并绘制，+/- 转向，[ ] 保存和恢复 Turtle 状态",
            VisibilityPrefix(layer) + "替换与最终几何都由生产展开器和 Turtle 解释器生成；批次只控制展示进度。",
            frames.AsReadOnly());
    }

    private PathGeometry? TryInterpret(
        LSystemDefinition definition,
        string symbols,
        CancellationToken cancellationToken)
    {
        if (!symbols.Any(symbol => symbol is 'F' or 'G'))
        {
            return null;
        }

        return turtleInterpreter.Interpret(definition, symbols, cancellationToken);
    }

    private static string DescribeGeneration(LSystemDefinition definition, int generation, string symbols)
    {
        var preview = symbols.Length <= 72 ? symbols : symbols[..69] + "…";
        var rules = string.Join("；", definition.Rules.Select(rule => $"{rule.Symbol}→{rule.Replacement}"));
        return $"第 {generation} 轮共有 {symbols.Length} 个符号：{preview}。规则：{rules}";
    }

    private static string DescribeSymbol(char symbol) => symbol switch
    {
        'F' or 'G' => "向前并画线",
        'f' => "只向前移动",
        '+' => "按设定角度右转",
        '-' => "按设定角度左转",
        '[' => "保存当前位置和方向并进入分支",
        ']' => "恢复此前状态并结束分支",
        _ => "作为可继续替换的变量，不直接绘制"
    };

    private static RenderContext ResolveContext(ArtworkDefinition artwork, FractalLayerDefinition layer)
    {
        var selected = artwork.SelectLayer(layer.Id);
        return RenderContext.ForLayer(selected, layer, RenderContext.ForPreview(selected));
    }

    private static IReadOnlyList<MathLensSegment> ProjectSegments(
        IReadOnlyList<PathSegment> source,
        RenderContext context,
        LayerTransformDefinition transform) => source.Select(segment => new MathLensSegment(
            MathLensProjection.ToCanvas(segment.Start.X, segment.Start.Y, context, transform),
            MathLensProjection.ToCanvas(segment.End.X, segment.End.Y, context, transform))).ToArray();

    private static string VisibilityPrefix(FractalLayerDefinition layer) =>
        layer.IsVisible ? string.Empty : "当前层已隐藏；透镜仍按已保存配方解释。";
}

internal sealed class AttractorMathLensProvider(
    IAttractorPointCloudGenerator pointCloudGenerator,
    IEnumerable<IAttractorFormulaKernel> kernels) : IMathLensProvider
{
    private const int MaximumFrames = 240;
    private const int MaximumOverlayPoints = 20_000;
    private readonly IReadOnlyDictionary<AttractorFormula, IAttractorFormulaKernel> _kernels =
        kernels.ToDictionary(kernel => kernel.Formula);

    public bool Supports(FractalGeneratorKind kind) => kind == FractalGeneratorKind.StrangeAttractor;

    public async Task<MathLensAnalysis> AnalyzeAsync(
        ArtworkDefinition artwork,
        FractalLayerDefinition layer,
        MathLensSelection? selection,
        CancellationToken cancellationToken)
    {
        var selected = artwork.SelectLayer(layer.Id);
        var context = RenderContext.ForLayer(selected, layer, RenderContext.ForPreview(selected));
        var cloud = await pointCloudGenerator.GenerateAsync(
            layer.StrangeAttractor, layer.Seed, context, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_kernels.TryGetValue(layer.StrangeAttractor.Formula, out var kernel))
        {
            throw new NotSupportedException($"没有登记吸引子公式 {layer.StrangeAttractor.Formula}。");
        }

        var trajectory = BuildBurnInTrajectory(layer, kernel, cancellationToken);
        var projection = PointCloudProjection.Create(cloud, context.Width, context.Height);
        var trajectoryPoints = trajectory.Select(point =>
        {
            var mapped = projection.Map(new PointSample((float)point.X, (float)point.Y));
            return MathLensProjection.ToCanvas(
                mapped.X / Math.Max(1, context.Width - 1),
                mapped.Y / Math.Max(1, context.Height - 1),
                context,
                layer.Transform);
        }).ToArray();
        var trajectorySegments = trajectoryPoints.Zip(
            trajectoryPoints.Skip(1), (start, end) => new MathLensSegment(start, end)).ToArray();

        var overlayIndices = MathLensProjection.SampleIndices(cloud.Points.Count, MaximumOverlayPoints);
        var points = overlayIndices.Select(index =>
        {
            var mapped = projection.Map(cloud.Points[index]);
            return MathLensProjection.ToCanvas(
                mapped.X / Math.Max(1, context.Width - 1),
                mapped.Y / Math.Max(1, context.Height - 1),
                context,
                layer.Transform);
        }).ToArray();

        var frames = new List<MathLensFrame>();
        var burnInFrameBudget = Math.Min(60, MaximumFrames / 4);
        foreach (var index in MathLensProjection.SampleIndices(trajectoryPoints.Length, burnInFrameBudget))
        {
            frames.Add(new MathLensFrame(
                $"预热迭代 {index}",
                $"轨道 0 从稳定 Seed 初值出发，当前状态 ({trajectory[index].X:0.####}, {trajectory[index].Y:0.####})。预热点不会进入最终密度。",
                index,
                layer.StrangeAttractor.BurnInIterations,
                trajectorySegments,
                Math.Min(index, trajectorySegments.Length),
                [],
                0,
                trajectoryPoints[index]));
        }

        var formationFrames = Math.Max(1, MaximumFrames - frames.Count);
        foreach (var visible in MathLensProjection.SampleIndices(points.Length, formationFrames))
        {
            frames.Add(new MathLensFrame(
                $"点云形成 {visible + 1}/{points.Length}",
                $"正在显示生产点云的确定性抽样；实际预览使用 {cloud.Points.Count:N0} 点和固定 32 条逻辑轨道。",
                visible + 1,
                points.Length,
                [],
                0,
                points,
                visible + 1));
        }

        return new MathLensAnalysis(
            layer.Id,
            MathLensKind.AttractorFormation,
            $"{layer.Name} · 轨迹稳定与点云形成",
            Formula(layer.StrangeAttractor.Formula),
            (layer.IsVisible ? string.Empty : "当前层已隐藏；透镜仍按已保存配方解释。") +
            "公式策略、Seed 初值、预热和点云取景均与生产生成器共用；展示抽样不会改变密度计算。",
            frames.AsReadOnly());
    }

    private static IReadOnlyList<(double X, double Y)> BuildBurnInTrajectory(
        FractalLayerDefinition layer,
        IAttractorFormulaKernel kernel,
        CancellationToken cancellationToken)
    {
        var current = StrangeAttractorPointGenerator.CreateInitialState(layer.Seed, layer.StrangeAttractor.Formula, 0);
        var result = new List<(double X, double Y)>(layer.StrangeAttractor.BurnInIterations + 1);
        _ = StrangeAttractorPointGenerator.AdvanceBurnIn(
            layer.StrangeAttractor, kernel, current.X, current.Y, cancellationToken, result);
        return result.AsReadOnly();
    }

    private static string Formula(AttractorFormula formula) => formula switch
    {
        AttractorFormula.Clifford => "xₙ₊₁ = sin(a·yₙ) + c·cos(a·xₙ)；yₙ₊₁ = sin(b·xₙ) + d·cos(b·yₙ)",
        AttractorFormula.DeJong => "xₙ₊₁ = sin(a·yₙ) − cos(b·xₙ)；yₙ₊₁ = sin(c·xₙ) − cos(d·yₙ)",
        _ => throw new NotSupportedException($"不支持吸引子公式 {formula}。")
    };
}
