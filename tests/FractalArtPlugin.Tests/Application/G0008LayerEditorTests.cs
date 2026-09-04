using Xunit;

namespace FractalArtPlugin.Tests.Application;

public sealed class G0008LayerEditorTests
{
    private readonly ArtworkValidator _validator = new();

    [Fact]
    public void 增加排序分组移入移出均返回合法不可变作品()
    {
        var editor = new ArtworkLayerEditor(_validator);
        var original = ArtworkDefinition.CreateDefault();
        var withMandelbrot = editor.AddFractal(original, FractalGeneratorKind.Mandelbrot);
        var withGroup = editor.AddGroup(withMandelbrot);
        var group = Assert.IsType<LayerGroupDefinition>(withGroup.Layers[0]);
        var mandelbrot = ArtworkLayerTree.EnumerateFractals(withGroup.Layers)
            .Single(layer => layer.GeneratorKind == FractalGeneratorKind.Mandelbrot);

        var grouped = editor.MoveIntoGroup(withGroup, mandelbrot.Id, group.Id);
        var movedGroup = Assert.IsType<LayerGroupDefinition>(grouped.Layers[0]);
        Assert.Equal(mandelbrot.Id, Assert.Single(movedGroup.Children).Id);

        var movedDown = editor.Move(grouped, mandelbrot.Id, 1);
        var restored = editor.MoveOutOfGroup(movedDown, mandelbrot.Id);

        Assert.NotSame(original.Layers, withMandelbrot.Layers);
        Assert.Empty(Assert.IsType<LayerGroupDefinition>(restored.Layers[1]).Children);
        Assert.Contains(restored.Layers, layer => layer.Id == mandelbrot.Id);
        _validator.Validate(restored);
    }

    [Fact]
    public void 删除保护最后图层非空组及被引用遮罩源并列出目标()
    {
        var editor = new ArtworkLayerEditor(_validator);
        var single = ArtworkDefinition.CreateDefault();
        Assert.Contains("至少一个", Assert.Throws<InvalidOperationException>(() =>
            editor.Delete(single, "layer-1")).Message, StringComparison.Ordinal);

        var two = editor.AddFractal(single, FractalGeneratorKind.Mandelbrot);
        var source = two.SelectedFractalLayer;
        var target = ArtworkLayerTree.EnumerateFractals(two.Layers).Single(layer => layer.Id != source.Id);
        two = editor.Update(two, target with
        {
            Mask = new ScalarMaskDefinition(source.Id, 0.5, 0.1, false)
        });
        var referenced = Assert.Throws<InvalidOperationException>(() => editor.Delete(two, source.Id));
        Assert.Contains(target.Name, referenced.Message, StringComparison.Ordinal);

        var grouped = editor.AddGroup(two);
        var groupId = grouped.Presentation.SelectedLayerId;
        grouped = editor.MoveIntoGroup(grouped, target.Id, groupId);
        Assert.Contains("仍包含子图层", Assert.Throws<InvalidOperationException>(() =>
            editor.Delete(grouped, groupId)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 图层与组数量预算在领域服务边界生效()
    {
        var editor = new ArtworkLayerEditor(_validator);
        var artwork = ArtworkDefinition.CreateDefault();
        for (var index = 1; index < 8; index++)
        {
            artwork = editor.AddFractal(artwork, FractalGeneratorKind.Julia);
        }

        Assert.Throws<InvalidDataException>(() => editor.AddFractal(artwork, FractalGeneratorKind.Julia));
        for (var index = 0; index < 4; index++)
        {
            artwork = editor.AddGroup(artwork);
        }

        Assert.Throws<InvalidDataException>(() => editor.AddGroup(artwork));
    }
}
