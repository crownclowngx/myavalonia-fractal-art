using System.Text.Json;
using System.Text.Json.Nodes;
using MyAvaloniaManagement.PluginSdk;
using Xunit;

namespace FractalArtPlugin.Tests;

public sealed class G0009AttractorTests
{
    [Fact]
    public void 两种公式策略遵守各自递推定义()
    {
        var definition = ArtworkDefinition.CreateDefaultAttractor() with { A = 1.2, B = -0.7, C = 0.4, D = 1.8 };
        const double x = 0.25;
        const double y = -0.5;

        var clifford = new CliffordAttractorKernel().Step(definition, x, y);
        Assert.Equal(Math.Sin(1.2 * y) + 0.4 * Math.Cos(1.2 * x), clifford.X, 14);
        Assert.Equal(Math.Sin(-0.7 * x) + 1.8 * Math.Cos(-0.7 * y), clifford.Y, 14);

        var deJong = new DeJongAttractorKernel().Step(definition with { Formula = AttractorFormula.DeJong }, x, y);
        Assert.Equal(Math.Sin(1.2 * y) - Math.Cos(-0.7 * x), deJong.X, 14);
        Assert.Equal(Math.Sin(0.4 * x) - Math.Cos(1.8 * y), deJong.Y, 14);
    }

    [Theory]
    [InlineData(AttractorFormula.Clifford)]
    [InlineData(AttractorFormula.DeJong)]
    public async Task 固定逻辑轨道保证不同并发度逐点一致(AttractorFormula formula)
    {
        var generator = TestArtworkPipeline.CreateAttractorGenerator();
        var definition = ArtworkDefinition.CreateDefaultAttractor() with
        {
            Formula = formula,
            SampleCount = 10_000,
            BurnInIterations = 32
        };
        var first = await generator.GenerateAsync(definition, 42, Context(64, 48, 1), CancellationToken.None);
        foreach (var degree in new[] { 2, 4, 8 })
        {
            var parallel = await generator.GenerateAsync(
                definition,
                42,
                Context(64, 48, degree),
                CancellationToken.None);
            Assert.Equal(first.Points, parallel.Points);
            Assert.Equal(first.MinimumX, parallel.MinimumX);
            Assert.Equal(first.MaximumY, parallel.MaximumY);
        }

        Assert.All(first.Points, point =>
        {
            Assert.True(float.IsFinite(point.X));
            Assert.True(float.IsFinite(point.Y));
        });
    }

    [Fact]
    public async Task 不同Seed改变逻辑轨道初值()
    {
        var generator = TestArtworkPipeline.CreateAttractorGenerator();
        var definition = ArtworkDefinition.CreateDefaultAttractor() with { SampleCount = 10_000, BurnInIterations = 16 };

        var first = await generator.GenerateAsync(definition, 1, Context(64, 64, 2), CancellationToken.None);
        var second = await generator.GenerateAsync(definition, 2, Context(64, 64, 2), CancellationToken.None);

        Assert.NotEqual(first.Points[0], second.Points[0]);
    }

    [Fact]
    public async Task 密度累积在不同并发度下逐值一致且退化点云可居中()
    {
        var cloud = new PointCloud(Enumerable.Repeat(new PointSample(0, 0), 1000));
        var renderer = new PointDensityRenderer();
        var definition = ArtworkDefinition.CreateDefaultAttractor();

        var one = await renderer.RenderAsync(cloud, definition, Context(31, 17, 1), CancellationToken.None);
        foreach (var degree in new[] { 2, 4, 8 })
        {
            var parallel = await renderer.RenderAsync(
                cloud,
                definition,
                Context(31, 17, degree),
                CancellationToken.None);
            Assert.Equal(one.Values, parallel.Values);
            Assert.Equal(one.Escaped, parallel.Escaped);
        }

        Assert.True(one.Values.Max() > 0.99f);
        Assert.True(one.Escaped.Count(value => value) is >= 1 and <= 4);
        Assert.Equal(1000L * sizeof(float) * 2 + sizeof(float) * 4, cloud.EstimatedByteSize);

        var vertical = new PointCloud(Enumerable.Range(0, 1000)
            .Select(index => new PointSample(0, index / 999f * 2 - 1)));
        var verticalField = await renderer.RenderAsync(
            vertical,
            definition,
            Context(31, 17, 4),
            CancellationToken.None);
        Assert.All(
            verticalField.Escaped.Span.ToArray().Select((hit, index) => (hit, index)).Where(item => item.hit),
            item => Assert.Equal(15, item.index % 31));

        foreach (var boundary in new[]
        {
            definition with { Exposure = 0.1, Gamma = 0.2 },
            definition with { Exposure = 32, Gamma = 4 }
        })
        {
            var field = await renderer.RenderAsync(vertical, boundary, Context(31, 17, 4), CancellationToken.None);
            Assert.All(field.Values, value => Assert.True(float.IsFinite(value) && value is >= 0 and <= 1));
        }
    }

    [Fact]
    public void 密度渐变保持空白透明并以密度驱动Alpha()
    {
        var field = new ScalarField(2, 1, [0f, 1f], [false, true]);
        var gradient = new GradientDefinition(new(10, 20, 30, 0), new(100, 200, 250, 255), new(1, 2, 3));

        var image = new DensityGradientMapper().Map(field, gradient, CancellationToken.None);

        Assert.Equal([0, 0, 0, 0], image.Pixels.Take(4));
        Assert.Equal(255, image.Pixels[7]);
    }

    [Fact]
    public void 图层局部发光扩展Alpha但禁用时转交原图()
    {
        var pixels = new byte[9 * 9 * 4];
        var center = (4 * 9 + 4) * 4;
        pixels.AsSpan(center, 4).Fill(255);
        var source = new ImageSurface(9, 9, pixels);
        var renderer = new DensityGlowRenderer();
        var definition = ArtworkDefinition.CreateDefaultAttractor() with { GlowSigma = 1, GlowStrength = 1 };

        var glowing = renderer.Apply(source, definition, CancellationToken.None);

        Assert.Same(source, renderer.Apply(source, definition with { GlowEnabled = false }, CancellationToken.None));
        Assert.True(glowing.Pixels[(4 * 9 + 3) * 4 + 3] > 0);
        Assert.Equal(0, source.Pixels[(4 * 9 + 3) * 4 + 3]);
        Assert.Equal("d72a5c9a0eb59290", RenderFingerprint.Create(glowing));
    }

    [Fact]
    public async Task 规范创作图完整渲染并按参数边界复用缓存()
    {
        using var cache = new ArtworkGraphCache();
        var pipeline = TestArtworkPipeline.Create(cache: cache);
        var artwork = ArtworkDefinition.CreateDefault().WithGeneratorKind(FractalGeneratorKind.StrangeAttractor) with
        {
            StrangeAttractor = ArtworkDefinition.CreateDefaultAttractor() with
            {
                SampleCount = 10_000,
                BurnInIterations = 16,
                GlowEnabled = false
            }
        };
        var context = Context(64, 48, 4);
        Assert.Equal(
            [
                ArtworkGraphOperation.StrangeAttractorPoints,
                ArtworkGraphOperation.PointDensity,
                ArtworkGraphOperation.DensityGradient,
                ArtworkGraphOperation.DensityGlow,
                ArtworkGraphOperation.EffectChain,
                ArtworkGraphOperation.SingleLayerComposition,
                ArtworkGraphOperation.Output
            ],
            artwork.Graph.Nodes.Select(node => node.Operation));

        var first = await pipeline.RenderAsync(artwork, context, CancellationToken.None);
        var second = await pipeline.RenderAsync(artwork, context, CancellationToken.None);
        var recolored = await pipeline.RenderAsync(artwork with
        {
            Gradient = artwork.Gradient with { End = new RgbaColor(255, 0, 128) }
        }, context, CancellationToken.None);

        Assert.Equal(64 * 48 * 4, first.Image.Pixels.Count);
        Assert.True(second.Execution.FullyFromCache);
        Assert.Contains("layer-1-generator", recolored.Execution.CacheHitNodeIds);
        Assert.Contains("layer-1-density", recolored.Execution.CacheHitNodeIds);
        Assert.DoesNotContain("layer-1-color", recolored.Execution.CacheHitNodeIds);

        var exposed = await pipeline.RenderAsync(artwork with
        {
            StrangeAttractor = artwork.StrangeAttractor with { Exposure = 2 }
        }, context, CancellationToken.None);
        Assert.Contains("layer-1-generator", exposed.Execution.CacheHitNodeIds);
        Assert.DoesNotContain("layer-1-density", exposed.Execution.CacheHitNodeIds);

        var reglowed = await pipeline.RenderAsync(artwork with
        {
            StrangeAttractor = artwork.StrangeAttractor with { GlowEnabled = true, GlowStrength = 1.2 }
        }, context, CancellationToken.None);
        Assert.Contains("layer-1-generator", reglowed.Execution.CacheHitNodeIds);
        Assert.Contains("layer-1-density", reglowed.Execution.CacheHitNodeIds);
        Assert.Contains("layer-1-color", reglowed.Execution.CacheHitNodeIds);
        Assert.DoesNotContain("layer-1-glow", reglowed.Execution.CacheHitNodeIds);

        var reshaped = await pipeline.RenderAsync(artwork with
        {
            StrangeAttractor = artwork.StrangeAttractor with { A = -1.3 }
        }, context, CancellationToken.None);
        Assert.DoesNotContain("layer-1-generator", reshaped.Execution.CacheHitNodeIds);
        Assert.DoesNotContain("layer-1-density", reshaped.Execution.CacheHitNodeIds);

        var rescheduled = await pipeline.RenderAsync(
            artwork,
            context with { MaxDegreeOfParallelism = 8 },
            CancellationToken.None);
        Assert.Contains("layer-1-generator", rescheduled.Execution.CacheHitNodeIds);
        Assert.Contains("layer-1-density", rescheduled.Execution.CacheHitNodeIds);

        foreach (var degree in new[] { 1, 2, 4, 8 })
        {
            using var independentCache = new ArtworkGraphCache();
            var independentPipeline = TestArtworkPipeline.Create(cache: independentCache);
            var independent = await independentPipeline.RenderAsync(
                artwork,
                context with { MaxDegreeOfParallelism = degree },
                CancellationToken.None);
            Assert.Equal(first.Image.Pixels, independent.Image.Pixels);
        }

        Assert.Equal("e05672ae5266a84d", RenderFingerprint.Create(first.Image));

        var firstLayer = artwork.SelectedFractalLayer;
        var otherLayer = ArtworkDefinition.CreateDefaultLayer("other", FractalGeneratorKind.StrangeAttractor) with
        {
            StrangeAttractor = ArtworkDefinition.CreateDefaultAttractor() with
            {
                SampleCount = 10_000,
                BurnInIterations = 16,
                GlowEnabled = false
            }
        };
        ArtworkDefinition Layered(FractalLayerDefinition foreground) => new(
            ArtworkDefinition.CurrentFormatVersion,
            new CanvasDefinition(64, 64, new RgbaColor(0, 0, 0)),
            new ArtworkPresentationDefinition("图层", false, foreground.Id),
            [foreground, otherLayer],
            EffectChainDefinition.CreateDefaultMaster());
        using var layerCache = new ArtworkGraphCache();
        var layerPipeline = TestArtworkPipeline.Create(cache: layerCache);
        _ = await layerPipeline.RenderAsync(Layered(firstLayer), context, CancellationToken.None);
        var localGlowChange = await layerPipeline.RenderAsync(
            Layered(firstLayer with
            {
                StrangeAttractor = firstLayer.StrangeAttractor with { GlowEnabled = true, GlowStrength = 1.3 }
            }),
            context,
            CancellationToken.None);
        Assert.DoesNotContain("layer-1-glow", localGlowChange.Execution.CacheHitNodeIds);
        Assert.Contains("other-glow", localGlowChange.Execution.CacheHitNodeIds);
    }

    [Fact]
    public async Task 吸引子密度可以成为其它图层遮罩源()
    {
        var source = ArtworkDefinition.CreateDefaultLayer("attractor", FractalGeneratorKind.StrangeAttractor) with
        {
            StrangeAttractor = ArtworkDefinition.CreateDefaultAttractor() with { SampleCount = 10_000 }
        };
        var target = ArtworkDefinition.CreateDefaultLayer("target", FractalGeneratorKind.Julia) with
        {
            Mask = new ScalarMaskDefinition(source.Id, 0.2, 0.1, false)
        };
        var artwork = new ArtworkDefinition(
            ArtworkDefinition.CurrentFormatVersion,
            new CanvasDefinition(128, 128, new RgbaColor(0, 0, 0)),
            new ArtworkPresentationDefinition("图层", false, target.Id),
            [target, source],
            EffectChainDefinition.CreateDefaultMaster());

        new ArtworkValidator().Validate(artwork);
        var rendered = await TestArtworkPipeline.Create().RenderAsync(
            artwork,
            Context(64, 64, 4),
            CancellationToken.None);
        Assert.Equal(64 * 64 * 4, rendered.Image.Pixels.Count);
    }

    [Fact]
    public void 吸引子资源门禁同时约束单层总采样与密度画布()
    {
        var validator = new ArtworkValidator();
        var invalidLayer = ArtworkDefinition.CreateDefault().WithGeneratorKind(FractalGeneratorKind.StrangeAttractor) with
        {
            StrangeAttractor = ArtworkDefinition.CreateDefaultAttractor() with { SampleCount = 2_000_001 }
        };
        Assert.Throws<InvalidDataException>(() => validator.Validate(invalidLayer));

        var tooLarge = ArtworkDefinition.CreateDefault().WithGeneratorKind(FractalGeneratorKind.StrangeAttractor) with
        {
            Canvas = new CanvasDefinition(4097, 4097, new RgbaColor(0, 0, 0))
        };
        Assert.Throws<InvalidDataException>(() => validator.Validate(tooLarge));

        var first = ArtworkDefinition.CreateDefaultLayer("first", FractalGeneratorKind.StrangeAttractor) with
        {
            StrangeAttractor = ArtworkDefinition.CreateDefaultAttractor() with { SampleCount = 2_000_000 }
        };
        var second = ArtworkDefinition.CreateDefaultLayer("second", FractalGeneratorKind.StrangeAttractor) with
        {
            StrangeAttractor = ArtworkDefinition.CreateDefaultAttractor() with { SampleCount = 2_000_000 }
        };
        var third = ArtworkDefinition.CreateDefaultLayer("third", FractalGeneratorKind.StrangeAttractor) with
        {
            StrangeAttractor = ArtworkDefinition.CreateDefaultAttractor() with { SampleCount = 10_000 }
        };
        ArtworkDefinition Layered(params FractalLayerDefinition[] layers) => new(
            ArtworkDefinition.CurrentFormatVersion,
            new CanvasDefinition(64, 64, new RgbaColor(0, 0, 0)),
            new ArtworkPresentationDefinition("图层", false, first.Id),
            layers,
            EffectChainDefinition.CreateDefaultMaster());

        validator.Validate(Layered(first, second, third with { IsVisible = false }));
        Assert.Throws<InvalidDataException>(() => validator.Validate(Layered(first, second, third)));
    }

    [Fact]
    public void 吸引子参数边界拒绝非法枚举非有限数和越界值()
    {
        var validator = new ArtworkValidator();
        var source = ArtworkDefinition.CreateDefault().WithGeneratorKind(FractalGeneratorKind.StrangeAttractor);
        var minimum = ArtworkDefinition.CreateDefaultAttractor() with
        {
            A = -4,
            B = -4,
            C = -4,
            D = -4,
            BurnInIterations = 16,
            SampleCount = 10_000,
            Exposure = 0.1,
            Gamma = 0.2,
            GlowSigma = 0.5,
            GlowStrength = 0
        };
        var maximum = minimum with
        {
            A = 4,
            B = 4,
            C = 4,
            D = 4,
            BurnInIterations = 4096,
            SampleCount = 2_000_000,
            Exposure = 32,
            Gamma = 4,
            GlowSigma = 10,
            GlowStrength = 4
        };
        validator.Validate(source with { StrangeAttractor = minimum });
        validator.Validate(source with { StrangeAttractor = maximum });

        StrangeAttractorDefinition[] invalid =
        [
            minimum with { Formula = (AttractorFormula)99 },
            minimum with { A = double.NaN },
            minimum with { D = 4.01 },
            minimum with { BurnInIterations = 15 },
            minimum with { SampleCount = 9_999 },
            minimum with { Exposure = double.PositiveInfinity },
            minimum with { Gamma = 0.19 },
            minimum with { GlowSigma = 10.01 },
            minimum with { GlowStrength = -0.01 }
        ];
        Assert.All(invalid, definition =>
            Assert.Throws<InvalidDataException>(() => validator.Validate(source with { StrangeAttractor = definition })));
    }

    [Fact]
    public void 预览点预算属于运行上下文且最终导出使用作品声明值()
    {
        var artwork = ArtworkDefinition.CreateDefault().WithGeneratorKind(FractalGeneratorKind.StrangeAttractor) with
        {
            StrangeAttractor = ArtworkDefinition.CreateDefaultAttractor() with { SampleCount = 1_000_000 }
        };

        Assert.Equal(100_000, RenderContext.ForPreview(artwork).PointSampleBudget);
        Assert.Equal(400_000, RenderContext.ForPreview(artwork with
        {
            Presentation = artwork.Presentation with { HighQualityPreview = true }
        }).PointSampleBudget);
        Assert.Equal(1_000_000, RenderContext.ForExport(artwork).PointSampleBudget);
        Assert.Equal(1_000_000, artwork.StrangeAttractor.SampleCount);
    }

    [Fact]
    public void V8吸引子往返且V7图层显式补默认配方()
    {
        var codec = new ArtworkSnapshotCodec(new ArtworkValidator());
        var baseSource = ArtworkDefinition.CreateDefault().WithGeneratorKind(FractalGeneratorKind.StrangeAttractor) with
        {
            StrangeAttractor = ArtworkDefinition.CreateDefaultAttractor() with
            {
                Formula = AttractorFormula.DeJong,
                A = 1.4,
                SampleCount = 20_000
            }
        };
        var recipe = baseSource.ToVariationRecipe();
        var source = baseSource with
        {
            Exploration = ArtworkExplorationDefinition.CreateDefault() with
            {
                Generation = 1,
                Candidates = Enumerable.Range(1, 9).Select(index =>
                    new VariationCandidateDefinition($"g000001-c{index:D2}", index, recipe)).ToArray(),
                Favorites = [new FavoriteVariationDefinition("fav-g000001-c01", "第 1 轮 · 变体 1", recipe)]
            }
        };
        var encoded = codec.Encode(source);
        var restored = codec.Decode(encoded);
        Assert.Equal(source.StrangeAttractor, restored.StrangeAttractor);
        Assert.Equal(source.Exploration.Candidates, restored.Exploration.Candidates);
        Assert.Equal(source.Exploration.Favorites, restored.Exploration.Favorites);

        var legacy = JsonNode.Parse(encoded.Payload.GetRawText())!.AsObject();
        legacy["formatVersion"] = 7;
        var legacyFractal = legacy["layers"]![0]!["fractal"]!.AsObject();
        legacyFractal.Remove("strangeAttractor");
        foreach (var candidate in legacyFractal["exploration"]!["candidates"]!.AsArray())
        {
            candidate!["recipe"]!.AsObject().Remove("strangeAttractor");
        }

        foreach (var favorite in legacyFractal["exploration"]!["favorites"]!.AsArray())
        {
            favorite!["recipe"]!.AsObject().Remove("strangeAttractor");
        }

        using var document = JsonDocument.Parse(legacy.ToJsonString());
        var migrated = codec.Decode(new DocumentContent(
            ArtworkSnapshotCodec.ContentSchemaVersion,
            document.RootElement.Clone()));

        Assert.Equal(ArtworkDefinition.CreateDefaultAttractor(), migrated.StrangeAttractor);
        Assert.All(migrated.Exploration.Candidates, candidate =>
            Assert.Equal(ArtworkDefinition.CreateDefaultAttractor(), candidate.Recipe.StrangeAttractor));
        Assert.All(migrated.Exploration.Favorites, favorite =>
            Assert.Equal(ArtworkDefinition.CreateDefaultAttractor(), favorite.Recipe.StrangeAttractor));
        Assert.Equal(ArtworkDefinition.CurrentFormatVersion, migrated.FormatVersion);

        var missing = JsonNode.Parse(encoded.Payload.GetRawText())!.AsObject();
        missing["layers"]![0]!["fractal"]!["strangeAttractor"]!.AsObject().Remove("gamma");
        using var missingDocument = JsonDocument.Parse(missing.ToJsonString());
        Assert.Throws<InvalidDataException>(() => codec.Decode(new DocumentContent(
            ArtworkSnapshotCodec.ContentSchemaVersion,
            missingDocument.RootElement.Clone())));

        var illegal = JsonNode.Parse(encoded.Payload.GetRawText())!.AsObject();
        illegal["layers"]![0]!["fractal"]!["strangeAttractor"]!["formula"] = 99;
        using var illegalDocument = JsonDocument.Parse(illegal.ToJsonString());
        Assert.Throws<InvalidDataException>(() => codec.Decode(new DocumentContent(
            ArtworkSnapshotCodec.ContentSchemaVersion,
            illegalDocument.RootElement.Clone())));
    }

    [Fact]
    public void 变体保持公式和采样预算但可改变形态与质感()
    {
        var source = ArtworkDefinition.CreateDefault().WithGeneratorKind(FractalGeneratorKind.StrangeAttractor) with
        {
            StrangeAttractor = ArtworkDefinition.CreateDefaultAttractor() with { SampleCount = 20_000 },
            Exploration = ArtworkExplorationDefinition.CreateDefault() with { MutationStrength = 1 }
        };
        var batch = new VariationGenerator(new ArtworkValidator()).Generate(source, 9);

        Assert.All(batch.Candidates, candidate =>
        {
            Assert.Equal(source.StrangeAttractor.Formula, candidate.Recipe.StrangeAttractor.Formula);
            Assert.Equal(source.StrangeAttractor.SampleCount, candidate.Recipe.StrangeAttractor.SampleCount);
            Assert.Equal(source.StrangeAttractor.BurnInIterations, candidate.Recipe.StrangeAttractor.BurnInIterations);
        });
        Assert.Contains(batch.Candidates, candidate => candidate.Recipe.StrangeAttractor != source.StrangeAttractor);
    }

    [Fact]
    public async Task 点云和密度循环响应预先取消()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var generator = TestArtworkPipeline.CreateAttractorGenerator();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generator.GenerateAsync(
            ArtworkDefinition.CreateDefaultAttractor() with { SampleCount = 10_000 },
            42,
            Context(64, 64, 4),
            cancellation.Token));

        var cloud = new PointCloud(Enumerable.Repeat(new PointSample(0, 0), 20_000));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PointDensityRenderer().RenderAsync(
            cloud,
            ArtworkDefinition.CreateDefaultAttractor(),
            Context(64, 64, 4),
            cancellation.Token));

        var image = new ImageSurface(9, 9, new byte[9 * 9 * 4]);
        Assert.ThrowsAny<OperationCanceledException>(() => new DensityGlowRenderer().Apply(
            image,
            ArtworkDefinition.CreateDefaultAttractor(),
            cancellation.Token));
    }

    private static RenderContext Context(int width, int height, int degree) => new(
        width,
        height,
        RenderQuality.Draft,
        42,
        RenderContext.CurrentRendererVersion,
        NumericPrecision.Double,
        16)
    {
        MaxDegreeOfParallelism = degree,
        PointSampleBudget = 10_000
    };
}
