using System.Globalization;
using FractalArtPlugin.Numerics;

namespace FractalArtPlugin.Domain.Artwork;

public enum MutationDistribution
{
    Uniform,
    Discrete,
    Logarithmic
}

/// <summary>
/// 参数变异契约。它让取值范围、步长、分布和所属分组成为可检查的数据，而不是散落在随机代码中的魔法数。
/// </summary>
public sealed record MutationParameterDescriptor(
    string Id,
    string DisplayName,
    VariationLockGroups Group,
    double Minimum,
    double Maximum,
    double Step,
    MutationDistribution Distribution);

public sealed record ArtisticParameters(int Detail, int Flow, int Curl);

public interface IArtisticParameterMapper
{
    ArtisticParameters Read(JuliaDefinition julia);
    JuliaDefinition SetDetail(JuliaDefinition julia, int value);
    JuliaDefinition SetFlow(JuliaDefinition julia, int value);
    JuliaDefinition SetCurl(JuliaDefinition julia, int value);
}

/// <summary>
/// 第一批艺术参数只投影到 Julia 的真实参数，不建立第二份可漂移状态。
/// “细节”映射迭代预算，“流动/卷曲”分别映射复常量实部/虚部；高级数学输入和艺术滑杆因此始终编辑同一事实。
/// </summary>
internal sealed class ArtisticParameterMapper : IArtisticParameterMapper
{
    private const int MinimumIterations = 64;
    private const int MaximumIterations = 1024;
    private const double ConstantLimit = 1.2;

    public ArtisticParameters Read(JuliaDefinition julia)
    {
        ArgumentNullException.ThrowIfNull(julia);
        return new ArtisticParameters(
            ToPercent(julia.MaxIterations, MinimumIterations, MaximumIterations),
            ToPercent(ArbitraryDecimal.Parse(julia.ConstantReal).ToDouble(), -ConstantLimit, ConstantLimit),
            ToPercent(ArbitraryDecimal.Parse(julia.ConstantImaginary).ToDouble(), -ConstantLimit, ConstantLimit));
    }

    public JuliaDefinition SetDetail(JuliaDefinition julia, int value)
    {
        var raw = MinimumIterations + ClampPercent(value) / 100d * (MaximumIterations - MinimumIterations);
        var stepped = Math.Clamp((int)Math.Round(raw / 16d) * 16, MinimumIterations, MaximumIterations);
        return julia with { MaxIterations = stepped };
    }

    public JuliaDefinition SetFlow(JuliaDefinition julia, int value) =>
        julia with { ConstantReal = FormatConstant(FromPercent(value, -ConstantLimit, ConstantLimit), julia.PrecisionDigits) };

    public JuliaDefinition SetCurl(JuliaDefinition julia, int value) =>
        julia with { ConstantImaginary = FormatConstant(FromPercent(value, -ConstantLimit, ConstantLimit), julia.PrecisionDigits) };

    private static int ToPercent(double value, double minimum, double maximum) =>
        Math.Clamp((int)Math.Round((value - minimum) / (maximum - minimum) * 100d), 0, 100);

    private static double FromPercent(int value, double minimum, double maximum) =>
        minimum + ClampPercent(value) / 100d * (maximum - minimum);

    private static int ClampPercent(int value) => Math.Clamp(value, 0, 100);

    private static string FormatConstant(double value, int precisionDigits) =>
        ArbitraryDecimal.Parse(value.ToString("G17", CultureInfo.InvariantCulture)).Round(precisionDigits).ToString();
}

public sealed record VariationBatch(int Generation, IReadOnlyList<VariationCandidateDefinition> Candidates);

public interface IVariationGenerator
{
    IReadOnlyList<MutationParameterDescriptor> Parameters { get; }
    VariationBatch Generate(ArtworkDefinition source, int candidateCount);
}

/// <summary>
/// 纯领域变异器。随机算法固定为本文件内的 SplitMix64，避免依赖 <see cref="Random"/> 的运行时实现细节；
/// 每个候选只由作品 Seed、持久化轮次和候选序号决定，因此跨进程可重现。
/// </summary>
internal sealed class VariationGenerator(IArtworkValidator validator) : IVariationGenerator
{
    private const int MinimumCandidateCount = 9;
    private const int MaximumCandidateCount = 12;

    public IReadOnlyList<MutationParameterDescriptor> Parameters { get; } =
    [
        new("julia.centerX", "构图中心 X", VariationLockGroups.Composition, -1, 1, 0, MutationDistribution.Uniform),
        new("julia.centerY", "构图中心 Y", VariationLockGroups.Composition, -1, 1, 0, MutationDistribution.Uniform),
        new("julia.scale", "构图尺度", VariationLockGroups.Composition, 0.55, 1.45, 0, MutationDistribution.Logarithmic),
        new("julia.constantReal", "流动", VariationLockGroups.Shape, -1.9, 1.9, 0, MutationDistribution.Uniform),
        new("julia.constantImaginary", "卷曲", VariationLockGroups.Shape, -1.9, 1.9, 0, MutationDistribution.Uniform),
        new("julia.maxIterations", "细节", VariationLockGroups.Shape, 16, 4096, 16, MutationDistribution.Discrete),
        new("mandelbrot.centerX", "Mandelbrot 中心 X", VariationLockGroups.Composition, -3, 3, 0, MutationDistribution.Uniform),
        new("mandelbrot.centerY", "Mandelbrot 中心 Y", VariationLockGroups.Composition, -3, 3, 0, MutationDistribution.Uniform),
        new("mandelbrot.scale", "Mandelbrot 构图尺度", VariationLockGroups.Composition, 0.55, 1.45, 0, MutationDistribution.Logarithmic),
        new("mandelbrot.maxIterations", "Mandelbrot 细节", VariationLockGroups.Shape, 16, 4096, 16, MutationDistribution.Discrete),
        new("tree.depth", "递归层级", VariationLockGroups.Shape, 1, 12, 1, MutationDistribution.Discrete),
        new("tree.branches", "每级分叉", VariationLockGroups.Shape, 2, 3, 1, MutationDistribution.Discrete),
        new("tree.angle", "分叉角度", VariationLockGroups.Shape, 5, 85, 0.5, MutationDistribution.Uniform),
        new("tree.lengthDecay", "长度衰减", VariationLockGroups.Shape, 0.45, 0.85, 0.01, MutationDistribution.Uniform),
        new("tree.randomness", "随机度", VariationLockGroups.Shape, 0, 1, 0.01, MutationDistribution.Uniform),
        new("lsystem.iterations", "L-System 迭代层级", VariationLockGroups.Shape, 0, 12, 1, MutationDistribution.Discrete),
        new("lsystem.angle", "L-System 转角", VariationLockGroups.Shape, 1, 360, 0.5, MutationDistribution.Uniform),
        new("attractor.a", "吸引子 A", VariationLockGroups.Shape, -4, 4, 0.01, MutationDistribution.Uniform),
        new("attractor.b", "吸引子 B", VariationLockGroups.Shape, -4, 4, 0.01, MutationDistribution.Uniform),
        new("attractor.c", "吸引子 C", VariationLockGroups.Shape, -4, 4, 0.01, MutationDistribution.Uniform),
        new("attractor.d", "吸引子 D", VariationLockGroups.Shape, -4, 4, 0.01, MutationDistribution.Uniform),
        new("attractor.exposure", "密度曝光", VariationLockGroups.Color, 0.1, 32, 0.1, MutationDistribution.Logarithmic),
        new("attractor.gamma", "密度 Gamma", VariationLockGroups.Color, 0.2, 4, 0.05, MutationDistribution.Uniform),
        new("gradient.rgb", "调色板", VariationLockGroups.Color, 0, 255, 1, MutationDistribution.Discrete),
        new("seed", "Seed", VariationLockGroups.Seed, long.MinValue, long.MaxValue, 1, MutationDistribution.Discrete)
    ];

    public VariationBatch Generate(ArtworkDefinition source, int candidateCount)
    {
        ArgumentNullException.ThrowIfNull(source);
        validator.Validate(source);
        if (candidateCount is < MinimumCandidateCount or > MaximumCandidateCount)
        {
            throw new ArgumentOutOfRangeException(nameof(candidateCount), "候选数量必须位于 9–12。 ");
        }

        var generation = checked(source.Exploration.Generation + 1);
        var candidates = new VariationCandidateDefinition[candidateCount];
        for (var index = 0; index < candidates.Length; index++)
        {
            var random = new StableRandom(CreateCandidateSeed(source.Seed, generation, index));
            var recipe = CreateValidRecipe(source, ref random);
            candidates[index] = new VariationCandidateDefinition(
                $"g{generation:D6}-c{index + 1:D2}",
                index + 1,
                recipe);
        }

        return new VariationBatch(generation, candidates);
    }

    /// <summary>
    /// 合法作品可以恰好位于中心、尺度或复常量边界。此时随机偏移可能越界，因此按同一稳定随机序列最多
    /// 重采样 8 次；极端情况下回退原配方，也绝不把不可渲染候选交给应用层。
    /// </summary>
    private VariationRecipeDefinition CreateValidRecipe(ArtworkDefinition source, ref StableRandom random)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var recipe = Mutate(source, ref random);
            try
            {
                validator.Validate(source.ApplyVariationRecipe(recipe));
                return recipe;
            }
            catch (InvalidDataException)
            {
                // 使用下一段确定性随机序列重新采样，不能通过截断高精度文本偷偷改变数值语义。
            }
        }

        return source.ToVariationRecipe();
    }

    private static VariationRecipeDefinition Mutate(ArtworkDefinition source, ref StableRandom random)
    {
        var settings = source.Exploration;
        var strength = settings.MutationStrength;
        var locks = settings.Locks;
        var mutateShape = settings.Mode is VariationMode.All or VariationMode.ShapeOnly;
        var mutateColor = settings.Mode is VariationMode.All or VariationMode.TextureOnly;
        var mutateComposition = settings.Mode == VariationMode.All;
        var julia = source.Julia;
        var mandelbrot = source.Mandelbrot;
        var recursiveTree = source.RecursiveTree;
        var lSystem = source.LSystem;
        var attractor = source.StrangeAttractor;
        var gradient = source.Gradient;
        var seed = source.Seed;

        if (source.GeneratorKind is FractalGeneratorKind.Julia or FractalGeneratorKind.RecursiveTree or
            FractalGeneratorKind.StrangeAttractor &&
            !locks.HasFlag(VariationLockGroups.Seed))
        {
            seed = unchecked((long)random.NextUInt64());
        }

        if (source.GeneratorKind == FractalGeneratorKind.Julia &&
            mutateComposition && !locks.HasFlag(VariationLockGroups.Composition))
        {
            julia = MutateComposition(julia, strength, ref random);
        }
        else if (source.GeneratorKind == FractalGeneratorKind.Mandelbrot &&
                 mutateComposition && !locks.HasFlag(VariationLockGroups.Composition))
        {
            mandelbrot = MutateComposition(mandelbrot, strength, ref random);
        }

        if (mutateShape && !locks.HasFlag(VariationLockGroups.Shape))
        {
            if (source.GeneratorKind == FractalGeneratorKind.RecursiveTree)
            {
                recursiveTree = MutateRecursiveTree(recursiveTree, strength, ref random);
            }
            else if (source.GeneratorKind == FractalGeneratorKind.Julia)
            {
                julia = MutateShape(julia, strength, ref random);
            }
            else if (source.GeneratorKind == FractalGeneratorKind.Mandelbrot)
            {
                mandelbrot = MutateShape(mandelbrot, strength, ref random);
            }
            else if (source.GeneratorKind == FractalGeneratorKind.LSystem)
            {
                lSystem = MutateLSystem(lSystem, strength, ref random);
            }
            else if (source.GeneratorKind == FractalGeneratorKind.StrangeAttractor)
            {
                attractor = MutateAttractorShape(attractor, strength, ref random);
            }
        }

        if (mutateColor && !locks.HasFlag(VariationLockGroups.Color))
        {
            gradient = MutateGradient(gradient, strength, ref random);
            if (source.GeneratorKind == FractalGeneratorKind.StrangeAttractor)
            {
                attractor = MutateAttractorTexture(attractor, strength, ref random);
            }
        }

        return new VariationRecipeDefinition(
            seed,
            source.GeneratorKind,
            julia,
            mandelbrot,
            recursiveTree,
            lSystem,
            attractor,
            gradient);
    }

    private static StrangeAttractorDefinition MutateAttractorShape(
        StrangeAttractorDefinition attractor,
        double strength,
        ref StableRandom random) => attractor with
        {
            // 公式、预热和采样预算保持不变；候选只改变可视形态，避免一次变体造成不可预期的性能跳变。
            A = Math.Clamp(attractor.A + random.NextSigned() * strength * 1.2, -4, 4),
            B = Math.Clamp(attractor.B + random.NextSigned() * strength * 1.2, -4, 4),
            C = Math.Clamp(attractor.C + random.NextSigned() * strength * 1.2, -4, 4),
            D = Math.Clamp(attractor.D + random.NextSigned() * strength * 1.2, -4, 4)
        };

    private static StrangeAttractorDefinition MutateAttractorTexture(
        StrangeAttractorDefinition attractor,
        double strength,
        ref StableRandom random) => attractor with
        {
            Exposure = Math.Clamp(attractor.Exposure * Math.Exp(random.NextSigned() * strength * 0.8), 0.1, 32),
            Gamma = Math.Clamp(attractor.Gamma + random.NextSigned() * strength * 0.8, 0.2, 4),
            GlowSigma = Math.Clamp(attractor.GlowSigma + random.NextSigned() * strength * 2, 0.5, 10),
            GlowStrength = Math.Clamp(attractor.GlowStrength + random.NextSigned() * strength, 0, 4)
        };

    private static MandelbrotDefinition MutateComposition(
        MandelbrotDefinition mandelbrot,
        double strength,
        ref StableRandom random)
    {
        var digits = mandelbrot.PrecisionDigits;
        var scale = ArbitraryDecimal.Parse(mandelbrot.Scale);
        var xOffset = scale.Multiply(ParseFactor(random.NextSigned() * strength * 0.35), digits);
        var yOffset = scale.Multiply(ParseFactor(random.NextSigned() * strength * 0.35), digits);
        var scaleFactor = ParseFactor(Math.Exp(random.NextSigned() * strength * 0.42));
        return mandelbrot with
        {
            CenterX = ArbitraryDecimal.Parse(mandelbrot.CenterX).Add(xOffset, digits).ToString(),
            CenterY = ArbitraryDecimal.Parse(mandelbrot.CenterY).Add(yOffset, digits).ToString(),
            Scale = scale.Multiply(scaleFactor, digits).ToString()
        };
    }

    private static MandelbrotDefinition MutateShape(
        MandelbrotDefinition mandelbrot,
        double strength,
        ref StableRandom random)
    {
        var iterationDelta = (int)Math.Round(random.NextSigned() * strength * 512d / 16d) * 16;
        return mandelbrot with
        {
            MaxIterations = Math.Clamp(mandelbrot.MaxIterations + iterationDelta, 16, 4096)
        };
    }

    /// <summary>
    /// 探索只扰动 L-System 的绘制参数，不改写用户规则文本。这样每个候选仍属于同一语法，
    /// 同时避免随机规则造成难以解释的语义突变和资源预算失控。
    /// </summary>
    private static LSystemDefinition MutateLSystem(
        LSystemDefinition lSystem,
        double strength,
        ref StableRandom random)
    {
        var iterationDelta = (int)Math.Round(random.NextSigned() * strength * 2);
        return lSystem with
        {
            Iterations = Math.Clamp(lSystem.Iterations + iterationDelta, 0, 12),
            TurnAngleDegrees = Math.Clamp(
                lSystem.TurnAngleDegrees + random.NextSigned() * strength * 35,
                1,
                360),
            InitialHeadingDegrees = Math.Clamp(
                lSystem.InitialHeadingDegrees + random.NextSigned() * strength * 45,
                -3_600,
                3_600),
            LengthDecay = Math.Clamp(
                lSystem.LengthDecay + random.NextSigned() * strength * 0.12,
                0.25,
                1)
        };
    }

    private static RecursiveTreeDefinition MutateRecursiveTree(
        RecursiveTreeDefinition tree,
        double strength,
        ref StableRandom random)
    {
        var depthDelta = (int)Math.Round(random.NextSigned() * strength * 3);
        var shouldSwitchBranches = random.NextSigned() * strength > 0.55;
        var branches = shouldSwitchBranches
            ? (tree.Branches == 2 ? 3 : 2)
            : tree.Branches;
        return tree with
        {
            Depth = Math.Clamp(tree.Depth + depthDelta, 1, 12),
            Branches = branches,
            BranchAngleDegrees = Math.Clamp(
                tree.BranchAngleDegrees + random.NextSigned() * strength * 28, 5, 85),
            LengthDecay = Math.Clamp(tree.LengthDecay + random.NextSigned() * strength * 0.16, 0.45, 0.85),
            Randomness = Math.Clamp(tree.Randomness + random.NextSigned() * strength * 0.45, 0, 1)
        };
    }

    private static JuliaDefinition MutateComposition(JuliaDefinition julia, double strength, ref StableRandom random)
    {
        var digits = julia.PrecisionDigits;
        var scale = ArbitraryDecimal.Parse(julia.Scale);
        var xOffset = scale.Multiply(ParseFactor(random.NextSigned() * strength * 0.35), digits);
        var yOffset = scale.Multiply(ParseFactor(random.NextSigned() * strength * 0.35), digits);
        var scaleFactor = ParseFactor(Math.Exp(random.NextSigned() * strength * 0.42));
        return julia with
        {
            CenterX = ArbitraryDecimal.Parse(julia.CenterX).Add(xOffset, digits).ToString(),
            CenterY = ArbitraryDecimal.Parse(julia.CenterY).Add(yOffset, digits).ToString(),
            Scale = scale.Multiply(scaleFactor, digits).ToString()
        };
    }

    private static JuliaDefinition MutateShape(JuliaDefinition julia, double strength, ref StableRandom random)
    {
        var real = Math.Clamp(ArbitraryDecimal.Parse(julia.ConstantReal).ToDouble() + random.NextSigned() * strength * 0.7, -1.9, 1.9);
        var imaginary = Math.Clamp(ArbitraryDecimal.Parse(julia.ConstantImaginary).ToDouble() + random.NextSigned() * strength * 0.7, -1.9, 1.9);
        var iterationDelta = (int)Math.Round(random.NextSigned() * strength * 512d / 16d) * 16;
        return julia with
        {
            ConstantReal = ParseFactor(real).Round(julia.PrecisionDigits).ToString(),
            ConstantImaginary = ParseFactor(imaginary).Round(julia.PrecisionDigits).ToString(),
            MaxIterations = Math.Clamp(julia.MaxIterations + iterationDelta, 16, 4096)
        };
    }

    private static GradientDefinition MutateGradient(GradientDefinition gradient, double strength, ref StableRandom random) =>
        gradient with
        {
            Start = MutateColor(gradient.Start, strength, ref random),
            End = MutateColor(gradient.End, strength, ref random)
        };

    private static RgbaColor MutateColor(RgbaColor color, double strength, ref StableRandom random)
    {
        var red = MutateChannel(color.Red, strength, random.NextSigned());
        var green = MutateChannel(color.Green, strength, random.NextSigned());
        var blue = MutateChannel(color.Blue, strength, random.NextSigned());
        return new RgbaColor(red, green, blue, color.Alpha);
    }

    private static byte MutateChannel(byte value, double strength, double signedRandom) =>
        (byte)Math.Clamp((int)Math.Round(value + signedRandom * strength * 128d), 0, 255);

    private static ArbitraryDecimal ParseFactor(double value) =>
        ArbitraryDecimal.Parse(value.ToString("G17", CultureInfo.InvariantCulture));

    private static ulong CreateCandidateSeed(long seed, int generation, int index)
    {
        var value = unchecked((ulong)seed);
        value ^= unchecked((ulong)generation * 0x9E3779B97F4A7C15UL);
        value ^= unchecked((ulong)(index + 1) * 0xD1B54A32D192ED03UL);
        return value;
    }

    private struct StableRandom(ulong state)
    {
        private ulong _state = state;

        public ulong NextUInt64()
        {
            _state += 0x9E3779B97F4A7C15UL;
            var value = _state;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }

        public double NextSigned() => (NextUInt64() >> 11) * (1d / (1UL << 53)) * 2d - 1d;
    }
}

public sealed record ArtworkPresetDefinition(
    string Id,
    string Name,
    string VisualFamily,
    FractalGeneratorKind GeneratorKind,
    JuliaDefinition Julia,
    RecursiveTreeDefinition RecursiveTree,
    GradientDefinition Gradient,
    MandelbrotDefinition? Mandelbrot = null,
    LSystemDefinition? LSystem = null,
    StrangeAttractorDefinition? StrangeAttractor = null);

public sealed record PalettePresetDefinition(string Id, string Name, GradientDefinition Gradient);

public interface IArtworkPresetCatalog
{
    IReadOnlyList<ArtworkPresetDefinition> ArtworkPresets { get; }
    IReadOnlyList<PalettePresetDefinition> PalettePresets { get; }
    ArtworkDefinition ApplyArtworkPreset(ArtworkDefinition artwork, string id);
    ArtworkDefinition ApplyPalettePreset(ArtworkDefinition artwork, string id);
}

/// <summary>首批预设是只读领域数据；应用预设仍然只修改真实 Julia/渐变参数，因此保存、撤销和导出无需特殊分支。</summary>
internal sealed class ArtworkPresetCatalog : IArtworkPresetCatalog
{
    public IReadOnlyList<ArtworkPresetDefinition> ArtworkPresets { get; } =
    [
        new("amber-bloom", "暮金花火", "无限与花朵", FractalGeneratorKind.Julia,
            new JuliaDefinition("0", "0", "3.2", "-0.745", "0.113", 384, false, 96),
            new RecursiveTreeDefinition(9, 2, 27, 0.72, 0.12, 0.28, 3.2),
            new GradientDefinition(new(18, 24, 58), new(255, 178, 72), new(2, 4, 12))),
        new("ocean-velvet", "深海丝绒", "无限与花朵", FractalGeneratorKind.Julia,
            new JuliaDefinition("0", "0", "3", "-0.4", "0.6", 448, false, 96),
            new RecursiveTreeDefinition(9, 2, 27, 0.72, 0.12, 0.28, 3.2),
            new GradientDefinition(new(4, 24, 52), new(42, 220, 210), new(1, 4, 12))),
        new("neon-coral", "霓虹珊瑚", "无限与花朵", FractalGeneratorKind.Julia,
            new JuliaDefinition("0", "0", "3.4", "-0.835", "-0.2321", 512, false, 96),
            new RecursiveTreeDefinition(9, 2, 27, 0.72, 0.12, 0.28, 3.2),
            new GradientDefinition(new(46, 12, 84), new(255, 73, 144), new(5, 1, 14))),
        new("verdant-growth", "翡翠生长", "植物与生长", FractalGeneratorKind.RecursiveTree,
            new JuliaDefinition("0", "0", "3.2", "-0.745", "0.113", 320, false, 96),
            new RecursiveTreeDefinition(10, 2, 25, 0.72, 0.1, 0.27, 4.2),
            new GradientDefinition(new(84, 48, 24), new(111, 238, 137), new(7, 15, 18))),
        new("winter-branches", "月下银枝", "植物与生长", FractalGeneratorKind.RecursiveTree,
            new JuliaDefinition("0", "0", "3.2", "-0.745", "0.113", 320, false, 96),
            new RecursiveTreeDefinition(8, 3, 31, 0.66, 0.18, 0.25, 3.5),
            new GradientDefinition(new(92, 105, 138), new(225, 242, 255), new(4, 8, 18))),
        new("mandelbrot-overview", "Mandelbrot 全景", "时间逃逸", FractalGeneratorKind.Mandelbrot,
            new JuliaDefinition("0", "0", "3.2", "-0.745", "0.113", 320, false, 96),
            new RecursiveTreeDefinition(9, 2, 27, 0.72, 0.12, 0.28, 3.2),
            new GradientDefinition(new(17, 25, 62), new(250, 177, 70), new(2, 4, 12)),
            new MandelbrotDefinition("-0.5", "0", "3", 384, false, 96)),
        new("mandelbrot-seahorse", "海马谷", "时间逃逸", FractalGeneratorKind.Mandelbrot,
            new JuliaDefinition("0", "0", "3.2", "-0.745", "0.113", 320, false, 96),
            new RecursiveTreeDefinition(9, 2, 27, 0.72, 0.12, 0.28, 3.2),
            new GradientDefinition(new(10, 32, 62), new(66, 236, 202), new(1, 5, 12)),
            new MandelbrotDefinition("-0.743643887037151", "0.13182590420533", "0.008", 768, false, 128)),
        new("mandelbrot-elephant", "象谷", "时间逃逸", FractalGeneratorKind.Mandelbrot,
            new JuliaDefinition("0", "0", "3.2", "-0.745", "0.113", 320, false, 96),
            new RecursiveTreeDefinition(9, 2, 27, 0.72, 0.12, 0.28, 3.2),
            new GradientDefinition(new(48, 14, 80), new(250, 97, 169), new(4, 1, 13)),
            new MandelbrotDefinition("0.285", "0.01", "0.08", 720, false, 112)),
        CreateLSystemPreset("lsystem-koch", "Koch 雪花", "F--F--F", [new('F', "F+F--F+F")], 4, 60, 0),
        CreateLSystemPreset("lsystem-dragon", "Heighway 龙", "FX", [new('X', "X+YF+"), new('Y', "-FX-Y")], 12, 90, 0),
        CreateLSystemPreset("lsystem-sierpinski", "Sierpiński 三角", "F-G-G",
            [new('F', "F-G+F+G-F"), new('G', "GG")], 5, 120, 0),
        CreateLSystemPreset("lsystem-plant", "经典分形植物", "X",
            [new('X', "F+[[X]-X]-F[-FX]+X"), new('F', "FF")], 5, 25, -90),
        CreateLSystemPreset("lsystem-hilbert", "Hilbert 曲线", "A",
            [new('A', "-BF+AFA+FB-"), new('B', "+AF-BFB-FA+")], 5, 90, 0),
        CreateAttractorPreset("attractor-aurora", "极光织网", AttractorFormula.Clifford, -1.4, 1.6, 1.0, 0.7,
            new GradientDefinition(new(24, 47, 89, 0), new(146, 240, 255), new(0, 0, 0, 0))),
        CreateAttractorPreset("attractor-silk", "丝绸星云", AttractorFormula.Clifford, -1.7, 1.8, -0.9, -0.4,
            new GradientDefinition(new(40, 18, 82, 0), new(255, 116, 205), new(0, 0, 0, 0))),
        CreateAttractorPreset("attractor-stardust", "星尘花冠", AttractorFormula.DeJong, 1.4, -2.3, 2.4, -2.1,
            new GradientDefinition(new(15, 52, 88, 0), new(255, 218, 122), new(0, 0, 0, 0))),
        CreateAttractorPreset("attractor-abyss", "深海回环", AttractorFormula.DeJong, -2, -2, -1.2, 2,
            new GradientDefinition(new(3, 34, 68, 0), new(83, 241, 213), new(0, 0, 0, 0)))
    ];

    public IReadOnlyList<PalettePresetDefinition> PalettePresets { get; } =
    [
        new("gold", "琥珀金", new GradientDefinition(new(20, 31, 74), new(248, 167, 63), new(3, 5, 12))),
        new("cyan", "极光青", new GradientDefinition(new(5, 27, 54), new(83, 241, 213), new(1, 5, 12))),
        new("magenta", "玫紫夜", new GradientDefinition(new(53, 13, 74), new(250, 89, 180), new(7, 2, 14)))
    ];

    public ArtworkDefinition ApplyArtworkPreset(ArtworkDefinition artwork, string id)
    {
        var preset = ArtworkPresets.SingleOrDefault(item => item.Id == id)
            ?? throw new ArgumentException($"未知作品预设：{id}。", nameof(id));
        return artwork.WithGeneratorKind(preset.GeneratorKind) with
        {
            Julia = preset.Julia,
            Mandelbrot = preset.Mandelbrot ?? artwork.Mandelbrot,
            RecursiveTree = preset.RecursiveTree,
            LSystem = preset.LSystem ?? artwork.LSystem,
            StrangeAttractor = preset.StrangeAttractor ?? artwork.StrangeAttractor,
            Gradient = preset.Gradient
        };
    }

    public ArtworkDefinition ApplyPalettePreset(ArtworkDefinition artwork, string id)
    {
        var preset = PalettePresets.SingleOrDefault(item => item.Id == id)
            ?? throw new ArgumentException($"未知调色板：{id}。", nameof(id));
        return artwork with { Gradient = preset.Gradient };
    }

    private static ArtworkPresetDefinition CreateLSystemPreset(
        string id,
        string name,
        string axiom,
        IReadOnlyList<LSystemRuleDefinition> rules,
        int iterations,
        double angle,
        double heading) => new(
            id,
            name,
            "递归路径",
            FractalGeneratorKind.LSystem,
            ArtworkDefinition.CreateDefault().Julia,
            ArtworkDefinition.CreateDefault().RecursiveTree,
            new GradientDefinition(new(24, 47, 43), new(126, 239, 153), new(3, 10, 12)),
            ArtworkDefinition.CreateDefault().Mandelbrot,
            new LSystemDefinition(axiom, rules, iterations, angle, heading, 0.02, 1, 2.8, 0.9));

    private static ArtworkPresetDefinition CreateAttractorPreset(
        string id,
        string name,
        AttractorFormula formula,
        double a,
        double b,
        double c,
        double d,
        GradientDefinition gradient) => new(
            id,
            name,
            "星云与粒子",
            FractalGeneratorKind.StrangeAttractor,
            ArtworkDefinition.CreateDefault().Julia,
            ArtworkDefinition.CreateDefault().RecursiveTree,
            gradient,
            ArtworkDefinition.CreateDefault().Mandelbrot,
            ArtworkDefinition.CreateDefault().LSystem,
            ArtworkDefinition.CreateDefaultAttractor() with { Formula = formula, A = a, B = b, C = c, D = d });
}
