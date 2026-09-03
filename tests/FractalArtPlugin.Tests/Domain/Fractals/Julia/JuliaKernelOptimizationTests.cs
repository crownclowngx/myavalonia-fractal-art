using Xunit;

namespace FractalArtPlugin.Tests.Domain.Fractals.Julia;

public sealed class JuliaKernelOptimizationTests
{
    [Fact]
    public async Task 串行与多工作线程对非整除行块产生逐值一致结果()
    {
        var definition = ArtworkDefinition.CreateDefault().Julia with
        {
            Scale = "1e-40",
            ForceHighPrecision = true,
            PrecisionDigits = 96,
            MaxIterations = 80
        };
        var common = new RenderContext(
            31, 23, RenderQuality.Final, 42, RenderContext.CurrentRendererVersion, NumericPrecision.Arbitrary, 64)
        {
            ConfiguredPrecisionDigits = 96,
            EffectivePrecisionDigits = 64,
            ChunkHeight = 7
        };
        var generator = new JuliaFieldGenerator();

        var serial = await generator.GenerateAsync(
            definition,
            common with { MaxDegreeOfParallelism = 1 },
            CancellationToken.None);
        var parallel = await generator.GenerateAsync(
            definition,
            common with { MaxDegreeOfParallelism = 4 },
            CancellationToken.None);

        Assert.Equal(serial.Escaped, parallel.Escaped);
        Assert.Equal(serial.Values, parallel.Values);
        Assert.Equal(1, serial.Diagnostics.MaxDegreeOfParallelism);
        Assert.Equal(4, parallel.Diagnostics.MaxDegreeOfParallelism);
    }

    [Fact]
    public async Task 重复运行结果与坐标锚点完全确定()
    {
        var definition = ArtworkDefinition.CreateDefault().Julia with
        {
            Scale = "1e-30",
            ForceHighPrecision = true,
            PrecisionDigits = 96,
            MaxIterations = 64
        };
        var context = new RenderContext(
            29, 19, RenderQuality.Final, 7, RenderContext.CurrentRendererVersion, NumericPrecision.Arbitrary, 64)
        {
            ConfiguredPrecisionDigits = 96,
            EffectivePrecisionDigits = 64,
            MaxDegreeOfParallelism = 3,
            ChunkHeight = 5
        };
        var generator = new JuliaFieldGenerator();

        var first = await generator.GenerateAsync(definition, context, CancellationToken.None);
        var second = await generator.GenerateAsync(definition, context, CancellationToken.None);

        Assert.Equal(first.Escaped, second.Escaped);
        Assert.Equal(first.Values, second.Values);
        Assert.Equal("arbitrary-fixed", first.Diagnostics.Kernel);
        Assert.Equal(64, first.Diagnostics.EffectivePrecisionDigits);
    }

    [Fact]
    public async Task 扰动实验路径与权威内核保持逃逸分类并在异常时可回退()
    {
        var definition = ArtworkDefinition.CreateDefault().Julia with
        {
            ForceHighPrecision = true,
            PrecisionDigits = 96,
            MaxIterations = 96
        };
        var common = new RenderContext(
            40, 28, RenderQuality.Final, 42, RenderContext.CurrentRendererVersion, NumericPrecision.Arbitrary, 64)
        {
            ConfiguredPrecisionDigits = 96,
            EffectivePrecisionDigits = 64,
            MaxDegreeOfParallelism = 4
        };
        var generator = new JuliaFieldGenerator();

        var reference = await generator.GenerateAsync(
            definition,
            common with { KernelPreference = JuliaKernelPreference.ReferenceArbitrary },
            CancellationToken.None);
        var perturbation = await generator.GenerateAsync(
            definition,
            common with { KernelPreference = JuliaKernelPreference.PerturbationExperiment },
            CancellationToken.None);

        Assert.Equal(reference.Escaped, perturbation.Escaped);
        for (var index = 0; index < reference.Values.Length; index++)
        {
            Assert.InRange(Math.Abs(reference.Values[index] - perturbation.Values[index]), 0f, 0.0001f);
        }

        Assert.Equal("perturbation-experiment", perturbation.Diagnostics.Kernel);
        Assert.InRange(perturbation.Diagnostics.PerturbationGlitchPixels, 0, perturbation.Values.Length);
    }

    [Fact]
    public async Task 极深缩放用扩展指数保留扰动量并与权威分类一致()
    {
        var definition = ArtworkDefinition.CreateDefault().Julia with
        {
            Scale = "1e-1000",
            ForceHighPrecision = true,
            PrecisionDigits = 1024,
            MaxIterations = 24
        };
        var context = new RenderContext(
            5, 5, RenderQuality.Final, 42, RenderContext.CurrentRendererVersion, NumericPrecision.Arbitrary, 1016)
        {
            ConfiguredPrecisionDigits = 1024,
            EffectivePrecisionDigits = 1016,
            KernelPreference = JuliaKernelPreference.PerturbationExperiment,
            MaxDegreeOfParallelism = 2
        };

        var generator = new JuliaFieldGenerator();
        var field = await generator.GenerateAsync(definition, context, CancellationToken.None);
        var reference = await generator.GenerateAsync(
            definition,
            context with { KernelPreference = JuliaKernelPreference.ReferenceArbitrary },
            CancellationToken.None);

        Assert.Equal(reference.Escaped, field.Escaped);
        Assert.InRange(field.Diagnostics.PerturbationGlitchPixels, 0, field.Values.Length);
    }

    [Fact]
    public async Task 逃逸阈值保护区会提升到配置精度并公开诊断()
    {
        var definition = ArtworkDefinition.CreateDefault().Julia with
        {
            CenterX = "2.0000000000000001",
            CenterY = "0",
            Scale = "1e-20",
            ConstantReal = "0",
            ConstantImaginary = "0",
            ForceHighPrecision = true,
            PrecisionDigits = 96,
            MaxIterations = 16
        };
        var context = new RenderContext(
            3, 3, RenderQuality.Final, 1, RenderContext.CurrentRendererVersion, NumericPrecision.Arbitrary, 32)
        {
            ConfiguredPrecisionDigits = 96,
            EffectivePrecisionDigits = 32,
            MaxDegreeOfParallelism = 2
        };

        var field = await new JuliaFieldGenerator().GenerateAsync(definition, context, CancellationToken.None);

        Assert.True(field.Diagnostics.PrecisionFallbackPixels > 0);
        Assert.All(field.Escaped, Assert.True);
    }

    [Fact]
    public async Task 动态精度与配置全精度参考保持逃逸分类和标量容差()
    {
        var definition = ArtworkDefinition.CreateDefault().Julia with
        {
            ForceHighPrecision = true,
            PrecisionDigits = 96,
            MaxIterations = 96
        };
        var common = new RenderContext(
            48, 32, RenderQuality.Final, 42, RenderContext.CurrentRendererVersion, NumericPrecision.Arbitrary, 32)
        {
            ConfiguredPrecisionDigits = 96,
            MaxDegreeOfParallelism = 4
        };
        var generator = new JuliaFieldGenerator();

        var dynamicPrecision = await generator.GenerateAsync(
            definition,
            common with { EffectivePrecisionDigits = 32 },
            CancellationToken.None);
        var configuredPrecision = await generator.GenerateAsync(
            definition,
            common with { PrecisionDigits = 96, EffectivePrecisionDigits = 96 },
            CancellationToken.None);

        Assert.Equal(configuredPrecision.Escaped, dynamicPrecision.Escaped);
        for (var index = 0; index < configuredPrecision.Values.Length; index++)
        {
            Assert.InRange(Math.Abs(configuredPrecision.Values[index] - dynamicPrecision.Values[index]), 0f, 0.0001f);
        }
    }

    [Fact]
    public async Task 长迭代在运行中取消会传播OperationCanceledException()
    {
        var definition = ArtworkDefinition.CreateDefault().Julia with
        {
            Scale = "1e-200",
            ForceHighPrecision = true,
            PrecisionDigits = 256,
            MaxIterations = 4096
        };
        var context = new RenderContext(
            64, 64, RenderQuality.Final, 42, RenderContext.CurrentRendererVersion, NumericPrecision.Arbitrary, 224)
        {
            ConfiguredPrecisionDigits = 256,
            EffectivePrecisionDigits = 224,
            CancellationCheckInterval = 16,
            MaxDegreeOfParallelism = 4
        };
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(20);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new JuliaFieldGenerator().GenerateAsync(definition, context, cancellation.Token));
    }

    [Fact]
    public async Task 自动策略只让草稿使用扰动而最终质量保留权威内核()
    {
        var definition = ArtworkDefinition.CreateDefault().Julia with
        {
            Scale = "1e-40",
            ForceHighPrecision = true,
            PrecisionDigits = 96,
            MaxIterations = 24
        };
        var common = new RenderContext(
            5, 5, RenderQuality.Draft, 1, RenderContext.CurrentRendererVersion, NumericPrecision.Arbitrary, 64)
        {
            ConfiguredPrecisionDigits = 96,
            EffectivePrecisionDigits = 64,
            MaxDegreeOfParallelism = 2
        };
        var generator = new JuliaFieldGenerator();

        var draft = await generator.GenerateAsync(definition, common, CancellationToken.None);
        var final = await generator.GenerateAsync(
            definition,
            common with { Quality = RenderQuality.Final },
            CancellationToken.None);

        Assert.Equal("perturbation-experiment", draft.Diagnostics.Kernel);
        Assert.Equal("arbitrary-fixed", final.Diagnostics.Kernel);
        Assert.Equal(final.Escaped, draft.Escaped);
    }
}
