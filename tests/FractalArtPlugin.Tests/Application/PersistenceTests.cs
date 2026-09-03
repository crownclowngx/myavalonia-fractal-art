using System.Text.Json;
using System.Text.Json.Nodes;
using FractalArtPlugin.Application;
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
            GeneratorKind = FractalGeneratorKind.RecursiveTree,
            Julia = new JuliaDefinition("-0.12", "0.34", "1.5e-80", "-0.8", "0.156", 777, true, 128),
            RecursiveTree = new RecursiveTreeDefinition(8, 3, 32, 0.66, 0.2, 0.24, 5.5),
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
        var json = valid.Payload.GetRawText().Replace("\"formatVersion\":4", "\"formatVersion\":99", StringComparison.Ordinal);
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
                formatVersion = 3,
                seed = 1,
                canvas = new { width = 800, height = 600, background = "#000000FF" }
            }));

        var exception = Assert.Throws<InvalidDataException>(() => _codec.Decode(content));

        Assert.Contains("缺少", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void V4缺失路径字段或使用未知生成器时不会静默采用默认值()
    {
        var encoded = _codec.Encode(ArtworkDefinition.CreateDefault());
        var missingTree = JsonNode.Parse(encoded.Payload.GetRawText())!.AsObject();
        missingTree.Remove("recursiveTree");
        var missingContent = new DocumentContent(
            ArtworkSnapshotCodec.ContentSchemaVersion,
            JsonSerializer.SerializeToElement(missingTree));

        Assert.Throws<InvalidDataException>(() => _codec.Decode(missingContent));

        var unknownGenerator = JsonNode.Parse(encoded.Payload.GetRawText())!.AsObject();
        unknownGenerator["generatorKind"] = 99;
        var unknownContent = new DocumentContent(
            ArtworkSnapshotCodec.ContentSchemaVersion,
            JsonSerializer.SerializeToElement(unknownGenerator));
        Assert.Throws<InvalidDataException>(() => _codec.Decode(unknownContent));
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
        Assert.Throws<InvalidDataException>(() => validator.Validate(
            defaultArtwork with
            {
                Exploration = defaultArtwork.Exploration with { MutationStrength = 0 }
            }));
        Assert.Throws<InvalidDataException>(() => validator.Validate(
            defaultArtwork with
            {
                Exploration = defaultArtwork.Exploration with
                {
                    Candidates = [new VariationCandidateDefinition("only-one", 1, defaultArtwork.ToVariationRecipe())]
                }
            }));
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

    [Fact]
    public void V4快照保存生成器路径与探索配方但不保存运行态对象()
    {
        var content = _codec.Encode(ArtworkDefinition.CreateDefault());
        var json = content.Payload.GetRawText();

        Assert.DoesNotContain("effectivePrecision", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("maxDegree", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("kernel", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("transient", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("previewImage", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, content.Payload.GetProperty("formatVersion").GetInt32());
        Assert.True(content.Payload.TryGetProperty("generatorKind", out _));
        Assert.True(content.Payload.TryGetProperty("recursiveTree", out _));
        Assert.True(content.Payload.TryGetProperty("exploration", out _));
    }

    [Fact]
    public void V2作品显式迁移为空探索状态且不改变渲染配方()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            formatVersion = 2,
            seed = 42,
            canvas = new { width = 800, height = 600, background = "#010203FF" },
            julia = new
            {
                centerX = "0",
                centerY = "0",
                scale = "3.2",
                constantReal = "-0.8",
                constantImaginary = "0.156",
                maxIterations = 320,
                forceHighPrecision = false,
                precisionDigits = 96
            },
            gradient = new { start = "#000000FF", end = "#FFFFFFFF", interior = "#010101FF" },
            presentation = new { selectedSection = "生成", highQualityPreview = false }
        });

        var migrated = _codec.Decode(new DocumentContent(ArtworkSnapshotCodec.ContentSchemaVersion, payload));

        Assert.Equal(ArtworkDefinition.CurrentFormatVersion, migrated.FormatVersion);
        Assert.Equal(42, migrated.Seed);
        Assert.Equal("-0.8", migrated.Julia.ConstantReal);
        Assert.Empty(migrated.Exploration.Candidates);
        Assert.Empty(migrated.Exploration.Favorites);
        Assert.Equal(0, migrated.Exploration.Generation);
        Assert.Equal(FractalGeneratorKind.Julia, migrated.GeneratorKind);
    }

    [Fact]
    public void V3作品及其候选显式迁移为Julia并补入安全路径默认值()
    {
        var source = ArtworkDefinition.CreateDefault();
        var recipe = source.ToVariationRecipe();
        source = source with
        {
            Exploration = source.Exploration with
            {
                Generation = 1,
                Candidates = Enumerable.Range(1, 9)
                    .Select(index => new VariationCandidateDefinition($"g000001-c{index:D2}", index, recipe))
                    .ToArray()
            }
        };
        var root = JsonNode.Parse(_codec.Encode(source).Payload.GetRawText())!.AsObject();
        root["formatVersion"] = 3;
        root.Remove("generatorKind");
        root.Remove("recursiveTree");
        foreach (var candidate in root["exploration"]!["candidates"]!.AsArray())
        {
            var candidateRecipe = candidate!["recipe"]!.AsObject();
            candidateRecipe.Remove("generatorKind");
            candidateRecipe.Remove("recursiveTree");
        }

        var v3Payload = JsonSerializer.SerializeToElement(root);

        var migrated = _codec.Decode(new DocumentContent(ArtworkSnapshotCodec.ContentSchemaVersion, v3Payload));

        Assert.Equal(ArtworkDefinition.CurrentFormatVersion, migrated.FormatVersion);
        Assert.Equal(FractalGeneratorKind.Julia, migrated.GeneratorKind);
        Assert.Equal(ArtworkDefinition.CreateDefault().RecursiveTree, migrated.RecursiveTree);
        Assert.Equal(9, migrated.Exploration.Candidates.Count);
        Assert.All(migrated.Exploration.Candidates, candidate =>
        {
            Assert.Equal(FractalGeneratorKind.Julia, candidate.Recipe.GeneratorKind);
            Assert.Equal(ArtworkDefinition.CreateDefault().RecursiveTree, candidate.Recipe.RecursiveTree);
        });
    }

    [Fact]
    public void 候选锁定与收藏配方完整往返()
    {
        var source = ArtworkDefinition.CreateDefault();
        var recipe = source.ToVariationRecipe() with { Seed = 99 };
        var expected = source with
        {
            Exploration = new ArtworkExplorationDefinition(
                0.6,
                VariationLockGroups.Seed | VariationLockGroups.Color,
                VariationMode.ShapeOnly,
                7,
                Enumerable.Range(1, 9).Select(index =>
                    new VariationCandidateDefinition($"g000007-c{index:D2}", index, recipe)).ToArray(),
                [new FavoriteVariationDefinition("fav-g000007-c01", "第 7 轮 · 变体 1", recipe)])
        };

        var restored = _codec.Decode(_codec.Encode(expected));

        Assert.Equal(expected.Exploration.MutationStrength, restored.Exploration.MutationStrength);
        Assert.Equal(expected.Exploration.Locks, restored.Exploration.Locks);
        Assert.Equal(expected.Exploration.Mode, restored.Exploration.Mode);
        Assert.Equal(expected.Exploration.Candidates, restored.Exploration.Candidates);
        Assert.Equal(expected.Exploration.Favorites, restored.Exploration.Favorites);
    }
}
