using Xunit;

namespace FractalArtPlugin.Tests.Domain.Artwork;

public sealed class G0008ArtworkValidatorTests
{
    private readonly ArtworkValidator _validator = new();

    [Fact]
    public void 重复ID悬空自引用和非标量遮罩源均给出带身份的中文诊断()
    {
        var first = Layer("same", "Julia A", FractalGeneratorKind.Julia);
        var duplicate = Layer("same", "Julia B", FractalGeneratorKind.Julia);
        var duplicateArtwork = Artwork([first, duplicate], first.Id);
        Assert.Contains("same", Assert.Throws<InvalidDataException>(() =>
            _validator.Validate(duplicateArtwork)).Message, StringComparison.Ordinal);

        var dangling = first with { Mask = new ScalarMaskDefinition("missing", 0.5, 0.1, false) };
        var danglingError = Assert.Throws<InvalidDataException>(() =>
            _validator.Validate(Artwork([dangling], dangling.Id)));
        Assert.Contains("Julia A", danglingError.Message, StringComparison.Ordinal);
        Assert.Contains("missing", danglingError.Message, StringComparison.Ordinal);

        var self = first with { Mask = new ScalarMaskDefinition(first.Id, 0.5, 0.1, false) };
        Assert.Contains("自身", Assert.Throws<InvalidDataException>(() =>
            _validator.Validate(Artwork([self], self.Id))).Message, StringComparison.Ordinal);

        var path = Layer("path", "树遮罩", FractalGeneratorKind.RecursiveTree);
        var target = first with { Id = "target", Mask = new ScalarMaskDefinition(path.Id, 0.5, 0.1, false) };
        Assert.Contains("树遮罩", Assert.Throws<InvalidDataException>(() =>
            _validator.Validate(Artwork([target, path], target.Id))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 变换效果与实际分形像素工作量预算不可绕过()
    {
        var invalidTransform = Layer("layer-1", "越界层", FractalGeneratorKind.Julia) with
        {
            Transform = LayerTransformDefinition.Identity with { ScalePercent = 0 }
        };
        Assert.Contains("越界层", Assert.Throws<InvalidDataException>(() =>
            _validator.Validate(Artwork([invalidTransform], invalidTransform.Id))).Message, StringComparison.Ordinal);

        var layers = Enumerable.Range(1, 5)
            .Select(index => Layer($"layer-{index}", $"层 {index}", FractalGeneratorKind.Julia))
            .Cast<ArtworkLayerDefinition>().ToArray();
        var oversized = Artwork(layers, "layer-1") with
        {
            Canvas = new CanvasDefinition(4096, 4096, new RgbaColor(0, 0, 0))
        };
        Assert.Contains("64M", Assert.Throws<InvalidDataException>(() =>
            _validator.Validate(oversized)).Message, StringComparison.Ordinal);

        var bloom = Artwork([Layer("layer-1", "Bloom 层", FractalGeneratorKind.Julia)], "layer-1") with
        {
            Canvas = new CanvasDefinition(4096, 4097, new RgbaColor(0, 0, 0)),
            MasterEffects = new EffectChainDefinition(1,
            [
                new ToneEffectDefinition(false, 0, 0, 1),
                new BloomEffectDefinition(true, 0.7, 2, 1)
            ])
        };
        Assert.Contains("16,777,216", Assert.Throws<InvalidDataException>(() =>
            _validator.Validate(bloom)).Message, StringComparison.Ordinal);
    }

    private static ArtworkDefinition Artwork(IReadOnlyList<ArtworkLayerDefinition> layers, string selectedId) =>
        new(
            ArtworkDefinition.CurrentFormatVersion,
            new CanvasDefinition(64, 64, new RgbaColor(0, 0, 0)),
            new ArtworkPresentationDefinition("图层", false, selectedId),
            layers,
            EffectChainDefinition.CreateDefaultMaster());

    private static FractalLayerDefinition Layer(string id, string name, FractalGeneratorKind kind) =>
        ArtworkDefinition.CreateDefaultLayer(id, kind) with { Name = name };
}
