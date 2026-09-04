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
            Graph = ArtworkGraphFactory.Create(FractalGeneratorKind.RecursiveTree),
            Julia = new JuliaDefinition("-0.12", "0.34", "1.5e-80", "-0.8", "0.156", 777, true, 128),
            Mandelbrot = new MandelbrotDefinition("-0.743", "0.131", "0.004", 900, true, 160),
            RecursiveTree = new RecursiveTreeDefinition(8, 3, 32, 0.66, 0.2, 0.24, 5.5),
            LSystem = new LSystemDefinition(
                "F--F--F",
                [new('F', "F+F--F+F")],
                3,
                60,
                0,
                0.02,
                1,
                2.5,
                0.9),
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
        var root = JsonNode.Parse(_codec.Encode(ArtworkDefinition.CreateDefault()).Payload.GetRawText())!.AsObject();
        root["formatVersion"] = 99;
        var content = new DocumentContent(
            ArtworkSnapshotCodec.ContentSchemaVersion,
            JsonSerializer.SerializeToElement(root));

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
    public void V5缺失生成器字段或使用未知生成器时不会静默采用默认值()
    {
        var missingTree = CreateLegacySnapshotNode(ArtworkDefinition.CreateDefault(), 5);
        missingTree.Remove("recursiveTree");
        var missingContent = new DocumentContent(
            ArtworkSnapshotCodec.ContentSchemaVersion,
            JsonSerializer.SerializeToElement(missingTree));

        Assert.Throws<InvalidDataException>(() => _codec.Decode(missingContent));

        var unknownGenerator = CreateLegacySnapshotNode(ArtworkDefinition.CreateDefault(), 5);
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
        Assert.False(migrated.HasLegacyGraphOverride);
    }

    [Fact]
    public void V8快照保存图层树吸引子与MasterEffects但不保存运行态对象()
    {
        var content = _codec.Encode(ArtworkDefinition.CreateDefault());
        var json = content.Payload.GetRawText();

        Assert.DoesNotContain("effectivePrecision", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("maxDegree", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("kernel", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("transient", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("previewImage", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(8, content.Payload.GetProperty("formatVersion").GetInt32());
        var layer = Assert.Single(content.Payload.GetProperty("layers").EnumerateArray());
        Assert.Equal("fractal", layer.GetProperty("typeId").GetString());
        Assert.True(layer.GetProperty("fractal").TryGetProperty("generatorKind", out _));
        Assert.True(layer.GetProperty("fractal").TryGetProperty("exploration", out _));
        Assert.True(layer.GetProperty("fractal").TryGetProperty("strangeAttractor", out _));
        Assert.False(content.Payload.TryGetProperty("graph", out _));
        Assert.Equal(2, content.Payload.GetProperty("masterEffects").GetProperty("effects").GetArrayLength());
    }

    [Fact]
    public void V5作品迁移为匹配生成器的规范图和空效果链()
    {
        var source = ArtworkDefinition.CreateDefault().WithGeneratorKind(FractalGeneratorKind.Mandelbrot);
        var root = CreateLegacySnapshotNode(source, 5);

        var migrated = _codec.Decode(new DocumentContent(
            ArtworkSnapshotCodec.ContentSchemaVersion,
            JsonSerializer.SerializeToElement(root)));

        Assert.Equal(ArtworkDefinition.CurrentFormatVersion, migrated.FormatVersion);
        Assert.Equal(ArtworkGraphFactory.Create(migrated.SelectedFractalLayer), migrated.Graph);
        Assert.All(migrated.MasterEffects.Effects, effect => Assert.False(effect.IsEnabled));
    }

    [Fact]
    public void V6缺失图未知图版本循环和未知效果都被明确拒绝()
    {
        var missing = CreateLegacySnapshotNode(ArtworkDefinition.CreateDefault(), 6);
        missing.Remove("graph");
        Assert.Throws<InvalidDataException>(() => DecodeNode(missing));

        var unknownVersion = CreateLegacySnapshotNode(ArtworkDefinition.CreateDefault(), 6);
        unknownVersion["graph"]!["version"] = 99;
        var versionError = Assert.Throws<ArtworkGraphValidationException>(() => DecodeNode(unknownVersion));
        Assert.Contains(versionError.Diagnostics, item => item.Code == "graph.version");

        var cycle = CreateLegacySnapshotNode(ArtworkDefinition.CreateDefault(), 6);
        var connections = cycle["graph"]!["connections"]!.AsArray();
        connections[1] = JsonNode.Parse(
            """{"sourceNodeId":"output","sourcePort":"image","targetNodeId":"effects","targetPort":"image"}""");
        var cycleError = Assert.Throws<ArtworkGraphValidationException>(() => DecodeNode(cycle));
        Assert.Contains(cycleError.Diagnostics, item => item.Code == "graph.cycle");

        var effect = CreateLegacySnapshotNode(ArtworkDefinition.CreateDefault(), 6);
        effect["effects"]!["effects"]!.AsArray().Add(JsonNode.Parse(
            """{"typeId":"future.glow","version":1,"isEnabled":true}"""));
        var effectError = Assert.Throws<NotSupportedException>(() => DecodeNode(effect));
        Assert.Contains("future.glow", effectError.Message, StringComparison.Ordinal);
    }

    private ArtworkDefinition DecodeNode(JsonObject root) => _codec.Decode(new DocumentContent(
        ArtworkSnapshotCodec.ContentSchemaVersion,
        JsonSerializer.SerializeToElement(root)));

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
        Assert.False(migrated.HasLegacyGraphOverride);
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
        var root = CreateLegacySnapshotNode(source, 3);
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
    public void V4递归树作品迁移时保持旧路径并补入新生成器默认值()
    {
        var expectedTree = new RecursiveTreeDefinition(7, 3, 34, 0.64, 0.18, 0.3, 4.5);
        var source = ArtworkDefinition.CreateDefault() with
        {
            GeneratorKind = FractalGeneratorKind.RecursiveTree,
            Graph = ArtworkGraphFactory.Create(FractalGeneratorKind.RecursiveTree),
            RecursiveTree = expectedTree
        };
        var root = CreateLegacySnapshotNode(source, 4);
        root.Remove("mandelbrot");
        root.Remove("lSystem");

        var migrated = _codec.Decode(new DocumentContent(
            ArtworkSnapshotCodec.ContentSchemaVersion,
            JsonSerializer.SerializeToElement(root)));

        Assert.Equal(ArtworkDefinition.CurrentFormatVersion, migrated.FormatVersion);
        Assert.Equal(FractalGeneratorKind.RecursiveTree, migrated.GeneratorKind);
        Assert.Equal(expectedTree, migrated.RecursiveTree);
        Assert.Equal(ArtworkDefinition.CreateDefault().Mandelbrot, migrated.Mandelbrot);
        Assert.Equal(ArtworkDefinition.CreateDefault().LSystem, migrated.LSystem);
    }

    [Fact]
    public void 候选锁定与收藏配方完整往返()
    {
        var source = ArtworkDefinition.CreateDefault();
        var recipe = source.ToVariationRecipe() with
        {
            Seed = 99,
            StrangeAttractor = ArtworkDefinition.CreateDefaultAttractor() with
            {
                Formula = AttractorFormula.DeJong,
                A = 1.4,
                Exposure = 2.5
            }
        };
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

    [Fact]
    public void V8完整往返保持分组顺序遮罩变换探索状态与MasterEffects()
    {
        var first = ArtworkDefinition.CreateDefaultLayer("julia-a", FractalGeneratorKind.Julia) with
        {
            Name = "前景 Julia",
            Opacity = 0.65,
            BlendMode = LayerBlendMode.Screen,
            Transform = new LayerTransformDefinition(12, -8, 135, 27, 40, 60),
            Mask = new ScalarMaskDefinition("mask-source", 0.42, 0.18, true),
            Exploration = ArtworkExplorationDefinition.CreateDefault() with { Generation = 7 }
        };
        var source = ArtworkDefinition.CreateDefaultLayer("mask-source", FractalGeneratorKind.Mandelbrot) with
        {
            Name = "遮罩源",
            IsVisible = false
        };
        var group = new LayerGroupDefinition(
            "group-1", "海报主体", true, 0.8, LayerBlendMode.Overlay,
            new LayerTransformDefinition(0, 5, 95, -12, 50, 50), null, [first]);
        var expected = new ArtworkDefinition(
            ArtworkDefinition.CurrentFormatVersion,
            new CanvasDefinition(800, 600, new RgbaColor(4, 5, 6)),
            new ArtworkPresentationDefinition("图层", true, first.Id),
            [group, source],
            new EffectChainDefinition(1,
            [
                new ToneEffectDefinition(true, 0.1, -0.2, 1.4),
                new BloomEffectDefinition(true, 0.7, 2.2, 1.1)
            ]));

        var restored = _codec.Decode(_codec.Encode(expected));

        Assert.Equal(expected, restored);
        Assert.Equal([group.Id, source.Id], restored.Layers.Select(layer => layer.Id));
        Assert.Equal(first.Id, Assert.Single(Assert.IsType<LayerGroupDefinition>(restored.Layers[0]).Children).Id);
    }

    [Fact]
    public void 未知图层和效果原样往返但统一阻止所有像素输出()
    {
        const string layerPayload = "{\"future\":{\"strength\":3},\"tokens\":[1,2]}";
        const string effectPayload = "{\"radius\":9,\"mode\":\"future\"}";
        var fallback = ArtworkDefinition.CreateDefaultLayer("layer-1", FractalGeneratorKind.Julia);
        var unavailable = new UnavailableLayerDefinition(
            "future-layer", "未来图层", true, 1, LayerBlendMode.Normal,
            LayerTransformDefinition.Identity, null, "future.fractal", 3, layerPayload);
        var group = new LayerGroupDefinition(
            "group-1", "兼容分组", true, 1, LayerBlendMode.Normal,
            LayerTransformDefinition.Identity, null, [unavailable]);
        var artwork = new ArtworkDefinition(
            ArtworkDefinition.CurrentFormatVersion,
            new CanvasDefinition(64, 64, new RgbaColor(0, 0, 0)),
            new ArtworkPresentationDefinition("图层", false, fallback.Id),
            [group, fallback],
            new EffectChainDefinition(1,
            [
                new ToneEffectDefinition(false, 0, 0, 1),
                new BloomEffectDefinition(false, 0.72, 2.4, 0.8),
                new UnavailableEffectDefinition("future.effect", 2, true, effectPayload)
            ]));

        var restored = _codec.Decode(_codec.Encode(artwork));
        var restoredLayer = Assert.IsType<UnavailableLayerDefinition>(
            Assert.Single(Assert.IsType<LayerGroupDefinition>(restored.Layers[0]).Children));
        var restoredEffect = Assert.IsType<UnavailableEffectDefinition>(restored.MasterEffects.Effects[2]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(layerPayload), JsonNode.Parse(restoredLayer.OpaquePayload)));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(effectPayload), JsonNode.Parse(restoredEffect.OpaquePayload)));

        var error = Assert.Throws<NotSupportedException>(() => new ArtworkValidator().EnsureRenderable(restored));
        Assert.Contains("未来图层", error.Message, StringComparison.Ordinal);
        Assert.Contains("future.effect", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 测试中的旧快照必须真实使用 v3-v6 的顶层结构，不能把当前 JSON 只改一个版本号后伪装成旧文件。
    /// 该辅助方法从当前层配方投影出历史字段，并仅在 v6 补入当时真实存在的规范图和空效果链。
    /// </summary>
    private JsonObject CreateLegacySnapshotNode(ArtworkDefinition artwork, int version)
    {
        var current = JsonNode.Parse(_codec.Encode(artwork).Payload.GetRawText())!.AsObject();
        var fractal = current["layers"]![0]!["fractal"]!.AsObject();
        var presentation = current["presentation"]!.DeepClone().AsObject();
        presentation.Remove("selectedLayerId");
        var root = new JsonObject
        {
            ["formatVersion"] = version,
            ["seed"] = fractal["seed"]!.DeepClone(),
            ["canvas"] = current["canvas"]!.DeepClone(),
            ["generatorKind"] = fractal["generatorKind"]!.DeepClone(),
            ["julia"] = fractal["julia"]!.DeepClone(),
            ["mandelbrot"] = fractal["mandelbrot"]!.DeepClone(),
            ["recursiveTree"] = fractal["recursiveTree"]!.DeepClone(),
            ["lSystem"] = fractal["lSystem"]!.DeepClone(),
            ["gradient"] = fractal["gradient"]!.DeepClone(),
            ["presentation"] = presentation,
            ["exploration"] = fractal["exploration"]!.DeepClone()
        };
        if (version == 6)
        {
            var graph = ArtworkGraphFactory.Create(artwork.GeneratorKind);
            root["graph"] = JsonSerializer.SerializeToNode(new
            {
                version = graph.Version,
                nodes = graph.Nodes.Select(node => new { id = node.Id, operation = (int)node.Operation, version = node.Version }),
                connections = graph.Connections.Select(connection => new
                {
                    sourceNodeId = connection.SourceNodeId,
                    sourcePort = connection.SourcePort,
                    targetNodeId = connection.TargetNodeId,
                    targetPort = connection.TargetPort
                }),
                outputNodeId = graph.OutputNodeId
            });
            root["effects"] = JsonNode.Parse("""{"version":1,"effects":[]}""");
        }

        return root;
    }
}
