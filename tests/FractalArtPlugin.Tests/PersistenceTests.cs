using System.Text.Json;
using FractalArtPlugin.Application;
using FractalArtPlugin.Domain;
using MyAvaloniaManagement.PluginSdk;
using Xunit;

namespace FractalArtPlugin.Tests;

public sealed class PersistenceTests
{
    private readonly ArtworkSnapshotCodec _codec = new(new ArtworkValidator());

    [Fact]
    public void 作品快照往返保持全部配方状态()
    {
        var expected = ArtworkDefinition.CreateDefault() with
        {
            Seed = 987654321,
            Canvas = new CanvasDefinition(2048, 1536, new RgbaColor(1, 2, 3, 4)),
            Julia = new JuliaDefinition("-0.12", "0.34", "1.5e-80", "-0.8", "0.156", 777, true, 128),
            Gradient = new GradientDefinition(
                new RgbaColor(10, 20, 30),
                new RgbaColor(200, 210, 220),
                new RgbaColor(2, 4, 8)),
            Presentation = new ArtworkPresentationDefinition("生成", true)
        };

        var restored = _codec.Decode(_codec.Encode(expected));

        Assert.Equal(expected, restored);
    }

    [Fact]
    public void 未知内容Schema被明确拒绝()
    {
        var payload = JsonSerializer.SerializeToElement(new { value = 1 });
        var content = new DocumentContent(99, payload);

        var exception = Assert.Throws<NotSupportedException>(() => _codec.Decode(content));

        Assert.Contains("schema 99", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 未知作品格式版本被明确拒绝且不会静默迁移()
    {
        var valid = _codec.Encode(ArtworkDefinition.CreateDefault());
        var json = valid.Payload.GetRawText().Replace("\"formatVersion\":2", "\"formatVersion\":99", StringComparison.Ordinal);
        using var payload = JsonDocument.Parse(json);
        var content = new DocumentContent(ArtworkSnapshotCodec.ContentSchemaVersion, payload.RootElement);

        var exception = Assert.Throws<NotSupportedException>(() => _codec.Decode(content));

        Assert.Contains("作品格式版本 99", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 缺失必要字段不会被默认值静默吞掉()
    {
        var content = new DocumentContent(
            ArtworkSnapshotCodec.ContentSchemaVersion,
            JsonSerializer.SerializeToElement(new
            {
                formatVersion = 2,
                seed = 1,
                canvas = new { width = 800, height = 600, background = "#000000FF" }
            }));

        var exception = Assert.Throws<InvalidDataException>(() => _codec.Decode(content));

        Assert.Contains("缺少", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 超预算画布和非法Julia参数在领域边界被拒绝()
    {
        var validator = new ArtworkValidator();
        var defaultArtwork = ArtworkDefinition.CreateDefault();

        Assert.Throws<InvalidDataException>(() => validator.Validate(
            defaultArtwork with { Canvas = defaultArtwork.Canvas with { Width = 10 } }));
        Assert.Throws<InvalidDataException>(() => validator.Validate(
            defaultArtwork with { Julia = defaultArtwork.Julia with { ConstantReal = "NaN" } }));
        Assert.Throws<InvalidDataException>(() => validator.Validate(
            defaultArtwork with { Julia = defaultArtwork.Julia with { CenterX = "1e-100000" } }));
    }

    [Fact]
    public void V1双精度作品被显式迁移为V2高精度字符串()
    {
        var legacyPayload = JsonSerializer.SerializeToElement(new
        {
            formatVersion = 1,
            seed = 42,
            canvas = new { width = 800, height = 600, background = "#010203FF" },
            julia = new
            {
                centerX = -0.12,
                centerY = 0.34,
                scale = 1.5,
                constantReal = -0.8,
                constantImaginary = 0.156,
                maxIterations = 320
            },
            gradient = new { start = "#000000FF", end = "#FFFFFFFF", interior = "#010101FF" },
            presentation = new { selectedSection = "生成", highQualityPreview = false }
        });

        var migrated = _codec.Decode(new DocumentContent(ArtworkSnapshotCodec.ContentSchemaVersion, legacyPayload));

        Assert.Equal(ArtworkDefinition.CurrentFormatVersion, migrated.FormatVersion);
        Assert.Equal(-0.12, ArbitraryDecimal.Parse(migrated.Julia.CenterX).ToDouble(), 12);
        Assert.Equal(96, migrated.Julia.PrecisionDigits);
        Assert.False(migrated.Julia.ForceHighPrecision);
    }
}
