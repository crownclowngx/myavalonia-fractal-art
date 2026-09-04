namespace FractalArtPlugin.Tests;

/// <summary>测试统一使用与生产注册相同的节点集合，避免保留一条已经不存在的生成器旁路。</summary>
internal static class TestArtworkPipeline
{
    public static IArtworkRenderPipeline Create(
        ILSystemValidator? lSystemValidator = null,
        IArtworkGraphCache? cache = null)
    {
        var graphValidator = new ArtworkGraphValidator();
        var rules = lSystemValidator ?? new LSystemValidator();
        IArtworkGraphNodeExecutor[] executors =
        [
            new JuliaFieldNodeExecutor(new JuliaFieldGenerator()),
            new MandelbrotFieldNodeExecutor(new MandelbrotFieldGenerator()),
            new RecursiveTreePathNodeExecutor(new RecursiveTreePathGenerator()),
            new LSystemPathNodeExecutor(new LSystemExpander(rules), new TurtlePathInterpreter()),
            new StrangeAttractorPointsNodeExecutor(CreateAttractorGenerator()),
            new PointDensityNodeExecutor(new PointDensityRenderer()),
            new ScalarGradientNodeExecutor(new LinearGradientMapper()),
            new DensityGradientNodeExecutor(new DensityGradientMapper()),
            new DensityGlowNodeExecutor(new DensityGlowRenderer()),
            new PathStrokeNodeExecutor(new PathStrokeRenderer()),
            new EffectChainNodeExecutor(),
            new SingleLayerCompositionNodeExecutor(),
            new OutputNodeExecutor()
        ];
        var executor = new ArtworkGraphExecutor(graphValidator, cache ?? new ArtworkGraphCache(), executors);
        return new ArtworkRenderPipeline(new ArtworkValidator(rules, graphValidator), executor);
    }

    public static IAttractorPointCloudGenerator CreateAttractorGenerator() =>
        new StrangeAttractorPointGenerator([new CliffordAttractorKernel(), new DeJongAttractorKernel()]);
}
