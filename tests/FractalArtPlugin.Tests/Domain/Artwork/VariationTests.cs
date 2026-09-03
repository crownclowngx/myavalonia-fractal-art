using FractalArtPlugin.Application;
using Xunit;

namespace FractalArtPlugin.Tests;

public sealed class VariationTests
{
    private readonly ArtworkValidator _validator = new();

    [Fact]
    public void 艺术参数直接稳定映射到Julia真实参数()
    {
        var mapper = new ArtisticParameterMapper();
        var original = ArtworkDefinition.CreateDefault().Julia;

        var changed = mapper.SetCurl(mapper.SetFlow(mapper.SetDetail(original, 75), 25), 80);
        var projected = mapper.Read(changed);

        Assert.InRange(projected.Detail, 74, 76);
        Assert.Equal(25, projected.Flow);
        Assert.Equal(80, projected.Curl);
        Assert.InRange(changed.MaxIterations, 64, 1024);
        Assert.NotEqual(original.ConstantReal, changed.ConstantReal);
        Assert.NotEqual(original.ConstantImaginary, changed.ConstantImaginary);
    }

    [Fact]
    public void 相同作品轮次与Seed生成完全相同的九个配方()
    {
        var generator = new VariationGenerator(_validator);
        var source = ArtworkDefinition.CreateDefault();

        var first = generator.Generate(source, 9);
        var second = generator.Generate(source, 9);

        Assert.Equal(1, first.Generation);
        Assert.Equal(first.Candidates, second.Candidates);
        Assert.Equal(9, first.Candidates.Select(item => item.Id).Distinct().Count());
        Assert.All(first.Candidates, candidate => _validator.Validate(
            source.ApplyVariationRecipe(candidate.Recipe)));

        var boundary = source with
        {
            Julia = source.Julia with
            {
                CenterX = "1e6",
                CenterY = "-1e6",
                Scale = "10",
                ConstantReal = "1.99",
                ConstantImaginary = "-1.99",
                MaxIterations = 4096
            }
        };
        var boundaryBatch = generator.Generate(boundary, 9);
        Assert.All(boundaryBatch.Candidates, candidate => _validator.Validate(
            boundary.ApplyVariationRecipe(candidate.Recipe)));
    }

    [Fact]
    public void 全部分组锁定后下一轮完整保持当前配方()
    {
        var generator = new VariationGenerator(_validator);
        var source = ArtworkDefinition.CreateDefault();
        source = source with
        {
            Exploration = source.Exploration with
            {
                MutationStrength = 1,
                Locks = VariationLockGroups.Seed | VariationLockGroups.Composition |
                    VariationLockGroups.Shape | VariationLockGroups.Color
            }
        };

        var batch = generator.Generate(source, 9);

        Assert.All(batch.Candidates, candidate => Assert.Equal(source.ToVariationRecipe(), candidate.Recipe));
    }

    [Fact]
    public void 只改变质感时构图和形态不变且元数据声明完整边界()
    {
        var generator = new VariationGenerator(_validator);
        var source = ArtworkDefinition.CreateDefault() with
        {
            Exploration = ArtworkDefinition.CreateDefault().Exploration with
            {
                Mode = VariationMode.TextureOnly,
                Locks = VariationLockGroups.Seed
            }
        };

        var batch = generator.Generate(source, 9);

        Assert.All(batch.Candidates, candidate =>
        {
            Assert.Equal(source.Julia, candidate.Recipe.Julia);
            Assert.Equal(source.Seed, candidate.Recipe.Seed);
        });
        Assert.Contains(batch.Candidates, candidate => candidate.Recipe.Gradient != source.Gradient);
        Assert.All(generator.Parameters, descriptor =>
        {
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Id));
            Assert.True(descriptor.Maximum >= descriptor.Minimum);
            Assert.NotEqual(VariationLockGroups.None, descriptor.Group);
        });
    }

    [Fact]
    public void 递归树变体覆盖路径参数且始终保持当前生成器与资源预算()
    {
        var generator = new VariationGenerator(_validator);
        var source = ArtworkDefinition.CreateDefault() with
        {
            GeneratorKind = FractalGeneratorKind.RecursiveTree,
            Exploration = ArtworkDefinition.CreateDefault().Exploration with { MutationStrength = 1 }
        };

        var first = generator.Generate(source, 9);
        var repeated = generator.Generate(source, 9);

        Assert.Equal(first.Candidates, repeated.Candidates);
        Assert.All(first.Candidates, candidate =>
        {
            Assert.Equal(FractalGeneratorKind.RecursiveTree, candidate.Recipe.GeneratorKind);
            Assert.Equal(source.Julia, candidate.Recipe.Julia);
            _validator.Validate(source.ApplyVariationRecipe(candidate.Recipe));
        });
        Assert.Contains(first.Candidates, candidate => candidate.Recipe.RecursiveTree != source.RecursiveTree);
        Assert.Contains(generator.Parameters, parameter => parameter.Id == "tree.depth");
        Assert.Contains(generator.Parameters, parameter => parameter.Id == "tree.branches");
        Assert.Contains(generator.Parameters, parameter => parameter.Id == "tree.angle");
        Assert.Contains(generator.Parameters, parameter => parameter.Id == "tree.lengthDecay");
        Assert.Contains(generator.Parameters, parameter => parameter.Id == "tree.randomness");
    }

    [Fact]
    public void 递归树只改变质感时路径配方保持不变()
    {
        var generator = new VariationGenerator(_validator);
        var source = ArtworkDefinition.CreateDefault() with
        {
            GeneratorKind = FractalGeneratorKind.RecursiveTree,
            Exploration = ArtworkDefinition.CreateDefault().Exploration with
            {
                Mode = VariationMode.TextureOnly,
                Locks = VariationLockGroups.Seed
            }
        };

        var batch = generator.Generate(source, 9);

        Assert.All(batch.Candidates, candidate =>
            Assert.Equal(source.RecursiveTree, candidate.Recipe.RecursiveTree));
        Assert.Contains(batch.Candidates, candidate => candidate.Recipe.Gradient != source.Gradient);
    }

    [Fact]
    public async Task 候选缩略图并发不超过三且重复批次全部命中缓存()
    {
        var pipeline = new MeasuringPipeline();
        var explorer = new VariationExplorer(new VariationGenerator(_validator), pipeline);
        var source = ArtworkDefinition.CreateDefault();

        var first = await explorer.ExploreAsync(source, 9, CancellationToken.None);
        var second = await explorer.ExploreAsync(source, 9, CancellationToken.None);

        Assert.Equal(9, pipeline.CallCount);
        Assert.InRange(pipeline.MaximumConcurrency, 1, 3);
        Assert.DoesNotContain(first.RenderedCandidates, item => item.FromCache);
        Assert.All(second.RenderedCandidates, item => Assert.True(item.FromCache));
    }

    [Fact]
    public async Task 取消候选渲染会终止整批且不会返回半批结果()
    {
        var pipeline = new MeasuringPipeline(delayMilliseconds: 500);
        var explorer = new VariationExplorer(new VariationGenerator(_validator), pipeline);
        using var cancellation = new CancellationTokenSource(30);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            explorer.ExploreAsync(ArtworkDefinition.CreateDefault(), 9, cancellation.Token));
    }

    private sealed class MeasuringPipeline(int delayMilliseconds = 15) : IArtworkRenderPipeline
    {
        private int _active;
        private int _maximumConcurrency;
        private int _callCount;

        public int CallCount => _callCount;
        public int MaximumConcurrency => _maximumConcurrency;

        public async Task<RgbaImage> RenderAsync(
            ArtworkDefinition artwork,
            RenderContext context,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            var active = Interlocked.Increment(ref _active);
            while (true)
            {
                var maximum = Volatile.Read(ref _maximumConcurrency);
                if (maximum >= active || Interlocked.CompareExchange(ref _maximumConcurrency, active, maximum) == maximum)
                {
                    break;
                }
            }

            try
            {
                await Task.Delay(delayMilliseconds, cancellationToken);
                return new RgbaImage(context.Width, context.Height, new byte[context.Width * context.Height * 4]);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }
}
