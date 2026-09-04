using Xunit;

namespace FractalArtPlugin.Tests.Domain.Rendering;

public sealed class GraphOutputCompatibilityTests
{
    [Theory]
    [InlineData("julia-default", "78088fc3fbed052b")]
    [InlineData("mandelbrot-overview", "b7c4c2bef3ef9121")]
    [InlineData("verdant-growth", "212294521abd5619")]
    [InlineData("lsystem-koch", "220170fe3299909a")]
    public async Task 四类生成器经过创作图后保持固定RGBA指纹(string id, string expected)
    {
        var canvas = new CanvasDefinition(96, 96, new RgbaColor(1, 2, 3));
        var artwork = ArtworkDefinition.CreateDefault() with { Canvas = canvas };
        if (id != "julia-default")
        {
            artwork = new ArtworkPresetCatalog().ApplyArtworkPreset(artwork, id);
        }

        var pipeline = TestArtworkPipeline.Create();
        var result = await pipeline.RenderAsync(artwork, RenderContext.ForExport(artwork), CancellationToken.None);

        Assert.Equal(expected, RenderFingerprint.Create(result.Image));
    }
}
