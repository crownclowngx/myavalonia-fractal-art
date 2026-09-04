using Xunit;

namespace FractalArtPlugin.Tests.Domain.Artwork;

public sealed class ArtworkGraphTests
{
    private readonly ArtworkGraphValidator _validator = new();

    [Theory]
    [InlineData(FractalGeneratorKind.Julia, ArtworkGraphOperation.JuliaField, ArtworkGraphOperation.ScalarGradient)]
    [InlineData(FractalGeneratorKind.Mandelbrot, ArtworkGraphOperation.MandelbrotField, ArtworkGraphOperation.ScalarGradient)]
    [InlineData(FractalGeneratorKind.RecursiveTree, ArtworkGraphOperation.RecursiveTreePath, ArtworkGraphOperation.PathStroke)]
    [InlineData(FractalGeneratorKind.LSystem, ArtworkGraphOperation.LSystemPath, ArtworkGraphOperation.PathStroke)]
    public void 四类规范图具有稳定类型与拓扑顺序(
        FractalGeneratorKind kind,
        ArtworkGraphOperation generator,
        ArtworkGraphOperation colorizer)
    {
        var graph = ArtworkGraphFactory.Create(kind);

        var ordered = _validator.ValidateAndSort(graph, kind, EffectChainDefinition.Empty);

        Assert.Equal(
            [generator, colorizer, ArtworkGraphOperation.EffectChain,
                ArtworkGraphOperation.SingleLayerComposition, ArtworkGraphOperation.Output],
            ordered.Select(item => item.Operation));
        Assert.Equal("output", graph.OutputNodeId);
    }

    [Fact]
    public void 重复节点缺失端点重复输入与类型不兼容均有定位诊断()
    {
        var canonical = ArtworkGraphFactory.Create(FractalGeneratorKind.Julia);
        var duplicateNode = new ArtworkGraphDefinition(
            canonical.Version,
            canonical.Nodes.Append(canonical.Nodes[0]),
            canonical.Connections,
            canonical.OutputNodeId);
        var missingEndpoint = new ArtworkGraphDefinition(
            canonical.Version,
            canonical.Nodes,
            canonical.Connections.Append(new("missing", "image", "output", "image")),
            canonical.OutputNodeId);
        var duplicateInput = new ArtworkGraphDefinition(
            canonical.Version,
            canonical.Nodes,
            canonical.Connections.Append(new("composition", "image", "output", "image")),
            canonical.OutputNodeId);
        var wrongType = new ArtworkGraphDefinition(
            canonical.Version,
            canonical.Nodes,
            canonical.Connections.Append(new("generator", "field", "effects", "image")),
            canonical.OutputNodeId);

        AssertDiagnostic(duplicateNode, "node.duplicate");
        AssertDiagnostic(missingEndpoint, "connection.endpoint");
        AssertDiagnostic(duplicateInput, "node.duplicate-input");
        AssertDiagnostic(wrongType, "connection.type");
    }

    [Fact]
    public void 未连接循环未知操作和未知版本不会进入执行阶段()
    {
        var canonical = ArtworkGraphFactory.Create(FractalGeneratorKind.Julia);
        var unconnected = new ArtworkGraphDefinition(
            canonical.Version,
            canonical.Nodes,
            canonical.Connections.Where(item => item.TargetNodeId != "color"),
            canonical.OutputNodeId);
        var cycle = new ArtworkGraphDefinition(
            canonical.Version,
            canonical.Nodes,
            canonical.Connections
                .Where(item => item.TargetNodeId != "effects")
                .Append(new("output", "image", "effects", "image")),
            canonical.OutputNodeId);
        var unknownOperationNodes = canonical.Nodes
            .Select(item => item.Id == "generator" ? item with { Operation = (ArtworkGraphOperation)999 } : item);
        var unknownOperation = new ArtworkGraphDefinition(
            canonical.Version,
            unknownOperationNodes,
            canonical.Connections,
            canonical.OutputNodeId);
        var unknownVersion = new ArtworkGraphDefinition(
            999,
            canonical.Nodes,
            canonical.Connections,
            canonical.OutputNodeId);
        var unknownNodeVersion = new ArtworkGraphDefinition(
            canonical.Version,
            canonical.Nodes.Select(item => item.Id == "generator" ? item with { Version = 2 } : item),
            canonical.Connections,
            canonical.OutputNodeId);

        AssertDiagnostic(unconnected, "node.unconnected");
        AssertDiagnostic(cycle, "graph.cycle");
        AssertDiagnostic(unknownOperation, "node.operation");
        AssertDiagnostic(unknownVersion, "graph.version");
        AssertDiagnostic(unknownNodeVersion, "node.version");
    }

    [Fact]
    public void 非空或未知版本效果链被明确拒绝()
    {
        var graph = ArtworkGraphFactory.Create(FractalGeneratorKind.Julia);
        var nonEmpty = new EffectChainDefinition(1, [new TestEffect("test.effect", 1, true)]);
        var unknownVersion = new EffectChainDefinition(99, []);

        AssertDiagnostic(graph, "effects.unsupported", nonEmpty);
        AssertDiagnostic(graph, "effects.version", unknownVersion);
    }

    [Fact]
    public void 图像标量场与遮罩不暴露可写缓存数组()
    {
        var pixels = new byte[] { 1, 2, 3, 4 };
        var values = new float[] { 0.5f };
        var escaped = new bool[] { true };
        var maskValues = new byte[] { 127 };
        var image = new ImageSurface(1, 1, pixels);
        var field = new ScalarField(1, 1, values, escaped);
        var mask = new Mask(1, 1, maskValues);

        pixels[0] = 99;
        values[0] = 1;
        escaped[0] = false;
        maskValues[0] = 0;
        var copiedPixels = image.Pixels.ToArray();
        copiedPixels[1] = 99;

        Assert.Equal((byte)1, image.Pixels[0]);
        Assert.Equal((byte)2, image.Pixels[1]);
        Assert.Equal(0.5f, field.Values[0]);
        Assert.True(field.Escaped[0]);
        Assert.Equal((byte)127, mask.Values[0]);
    }

    private void AssertDiagnostic(
        ArtworkGraphDefinition graph,
        string code,
        EffectChainDefinition? effects = null)
    {
        var exception = Assert.Throws<ArtworkGraphValidationException>(() =>
            _validator.ValidateAndSort(graph, FractalGeneratorKind.Julia, effects ?? EffectChainDefinition.Empty));
        Assert.Contains(exception.Diagnostics, item => item.Code == code);
        Assert.Contains("内部创作图", exception.Message, StringComparison.Ordinal);
    }

    private sealed record TestEffect(string Id, int EffectVersion, bool Enabled)
        : ArtworkEffectDefinition(Id, EffectVersion, Enabled);
}
