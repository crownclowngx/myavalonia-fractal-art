using Avalonia.Media.Imaging;
using FractalArtPlugin.Application;
using FractalArtPlugin.Application.Workflow;
using FractalArtPlugin.Features.Artwork;
using MyAvaloniaManagement.PluginSdk;
using Xunit;

namespace FractalArtPlugin.Tests;

public sealed class DocumentTests
{
    [Fact]
    public async Task 吸引子图层参数预设与撤销进入同一Document历史()
    {
        using var fixture = new DocumentFixture();
        using var document = fixture.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("吸引子编辑"), CancellationToken.None);

        document.AddLayerCommand.Execute("StrangeAttractor");
        Assert.True(document.IsAttractorGenerator);
        Assert.Equal(FractalGeneratorKind.StrangeAttractor, document.Artwork.GeneratorKind);
        Assert.True(document.IsSeedControlVisible);

        document.ApplyArtworkPresetCommand.Execute("attractor-stardust");
        Assert.Equal(AttractorFormula.DeJong, document.Artwork.StrangeAttractor.Formula);
        Assert.Equal(1.4, document.AttractorA, 12);
        document.AttractorExposure = 2.5;
        Assert.Equal(2.5, document.Artwork.StrangeAttractor.Exposure, 12);

        document.UndoCommand.Execute(null);
        Assert.NotEqual(2.5, document.Artwork.StrangeAttractor.Exposure);
        Assert.True(document.IsDirty);
    }

    [Fact]
    public async Task ImageLab导出参数不影响Dirty历史或ArtworkSnapshot()
    {
        using var fixture = new DocumentFixture();
        var document = fixture.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("效果会话"), CancellationToken.None);
        var before = await document.CaptureSaveSnapshotAsync(CancellationToken.None);

        document.ImageLabBlurEnabled = false;
        document.ImageLabBlurSigma = 8.5;
        document.ImageLabBloomThreshold = 0.25;
        document.ImageLabBloomStrength = 2.2;
        document.ImageLabGrainAmount = 12;
        document.ImageLabGrainSeed = 77;

        var after = await document.CaptureSaveSnapshotAsync(CancellationToken.None);
        Assert.False(document.IsDirty);
        Assert.False(document.CanUndo);
        Assert.Equal(before.Content.SchemaVersion, after.Content.SchemaVersion);
        Assert.Equal(before.Content.Payload.GetRawText(), after.Content.Payload.GetRawText());
    }

    [Fact]
    public async Task ImageLab不可用时不打开保存框也不调用协调器()
    {
        using var fixture = new DocumentFixture();
        var coordinator = new UnavailableImageLabCoordinator();
        var dialog = new RecordingImageLabDialog();
        var document = fixture.CreateDocument(coordinator, dialog);
        await document.InitializeAsync(new NewDocumentActivation("无 ImageLab"), CancellationToken.None);

        await document.ExportWithImageLabCommand.ExecuteAsync(null);

        Assert.Equal(0, dialog.Calls);
        Assert.Equal(0, coordinator.ExportCalls);
        Assert.Contains("不可用", document.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 新建修改保存确认和旧修订确认遵守Dirty语义()
    {
        using var fixture = new DocumentFixture();
        var document = fixture.CreateDocument();
        var dirtyTransitions = 0;
        document.IsDirtyChanged += (_, _) => dirtyTransitions++;
        await document.InitializeAsync(new NewDocumentActivation("测试作品"), CancellationToken.None);

        Assert.False(document.IsDirty);
        document.CanvasWidth = 1600;
        Assert.True(document.IsDirty);
        var oldSnapshot = await document.CaptureSaveSnapshotAsync(CancellationToken.None);
        document.CanvasHeight = 900;

        document.AcceptChanges(oldSnapshot.Revision);
        Assert.True(document.IsDirty);
        var latest = await document.CaptureSaveSnapshotAsync(CancellationToken.None);
        document.AcceptChanges(latest.Revision);

        Assert.False(document.IsDirty);
        Assert.Equal(2, dirtyTransitions);
    }

    [Fact]
    public async Task 保存后恢复保持构图且两个Document实例互相隔离()
    {
        using var firstFixture = new DocumentFixture();
        using var secondFixture = new DocumentFixture();
        var first = firstFixture.CreateDocument();
        var second = secondFixture.CreateDocument();
        await first.InitializeAsync(new NewDocumentActivation("作品 A"), CancellationToken.None);
        first.ConstantReal = "-0.91";
        first.ConstantImaginary = "0.22";
        first.Seed = 2027;
        var snapshot = await first.CaptureSaveSnapshotAsync(CancellationToken.None);

        await second.InitializeAsync(
            new RestoreDocumentActivation("作品 A 恢复", snapshot.Content),
            CancellationToken.None);

        Assert.Equal(first.Artwork, second.Artwork);
        Assert.False(second.IsDirty);
        second.Scale = "2.25";
        Assert.NotEqual(first.Scale, second.Scale);
        Assert.Equal("作品 A 恢复", second.Presentation.Title);
    }

    [Fact]
    public async Task 撤销重做只操作当前Document的不可变作品快照()
    {
        using var fixture = new DocumentFixture();
        var document = fixture.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("历史"), CancellationToken.None);
        var original = document.Scale;
        document.Scale = "2.4";

        document.UndoCommand.Execute(null);
        Assert.Equal(original, document.Scale);
        Assert.True(document.CanRedo);

        document.RedoCommand.Execute(null);
        Assert.Equal(2.4, ArbitraryDecimal.Parse(document.Scale).ToDouble(), 12);
        Assert.True(document.CanUndo);
    }

    [Fact]
    public async Task 损坏恢复在发布状态前失败且不会执行渲染()
    {
        using var fixture = new DocumentFixture();
        var document = fixture.CreateDocument();
        var content = new DocumentContent(1, System.Text.Json.JsonSerializer.SerializeToElement(new { formatVersion = 1 }));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await document.InitializeAsync(new RestoreDocumentActivation("坏作品", content), CancellationToken.None));

        Assert.Equal(0, fixture.Pipeline.CallCount);
        Assert.False(document.IsDirty);
    }

    [Fact]
    public async Task 快速连续改参时迟到旧画面不能覆盖最新画面()
    {
        var pipeline = new ControlledPipeline();
        using var fixture = new DocumentFixture(pipeline);
        var document = fixture.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("并发"), CancellationToken.None);

        document.ConstantReal = "-0.5";
        var oldTask = document.RenderPreviewNowAsync();
        await Task.Delay(10);
        document.ConstantReal = "-0.6";
        var latestTask = document.RenderPreviewNowAsync();
        await Task.WhenAll(oldTask, latestTask);

        var expected = RenderFingerprint.Create(ControlledPipeline.CreateImage("-0.6"));
        Assert.Equal(expected, document.LastPreviewFingerprint);
    }

    [Fact]
    public async Task 初始化和显式渲染都观察取消令牌()
    {
        using var fixture = new DocumentFixture();
        var document = fixture.CreateDocument();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await document.InitializeAsync(new NewDocumentActivation("取消"), cancellation.Token));

        Assert.Equal(0, fixture.Pipeline.CallCount);
    }

    [Fact]
    public async Task 连续拖动合并为一次撤销且滚轮改变高精度视口()
    {
        using var fixture = new DocumentFixture();
        var document = fixture.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("交互"), CancellationToken.None);
        var originalCenter = ArbitraryDecimal.Parse(document.CenterX);
        var originalScale = ArbitraryDecimal.Parse(document.Scale);

        document.BeginViewportInteraction();
        document.PanViewport(10, 0, 600);
        document.PanViewport(10, 0, 600);
        document.EndViewportInteraction();

        Assert.True(document.CanUndo);
        Assert.NotEqual(originalCenter, ArbitraryDecimal.Parse(document.CenterX));
        document.UndoCommand.Execute(null);
        Assert.Equal(originalCenter, ArbitraryDecimal.Parse(document.CenterX));
        Assert.False(document.CanUndo);

        document.ZoomViewport(400, 300, 800, 600, 1);
        Assert.True(ArbitraryDecimal.Parse(document.Scale).CompareTo(originalScale) < 0);
    }

    [Fact]
    public async Task 交互先更新暂态呈现而真实帧提交后复位()
    {
        var pipeline = new ProgressivePipeline();
        using var fixture = new DocumentFixture(pipeline);
        var document = fixture.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("渐进预览"), CancellationToken.None);

        document.BeginViewportInteraction();
        document.PanViewport(12, -7, 600);

        Assert.False(document.TransientPreview.IsIdentity);
        Assert.Equal(12d, document.TransientPreview.OffsetX);
        Assert.Equal(-7d, document.TransientPreview.OffsetY);
        await pipeline.WaitForCallsAsync(2).WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => document.TransientPreview.IsIdentity);
        document.EndViewportInteraction();
    }

    [Fact]
    public async Task 精细交互预览先提交低成本真实帧再提升质量()
    {
        var pipeline = new ProgressivePipeline();
        using var fixture = new DocumentFixture(pipeline);
        var document = fixture.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("两阶段"), CancellationToken.None);

        document.HighQualityPreview = true;
        await pipeline.WaitForCallsAsync(3).WaitAsync(TimeSpan.FromSeconds(5));

        var contexts = pipeline.Contexts.ToArray();
        Assert.Equal(480, contexts[0].Width); // 初始化时的普通草稿。
        Assert.Equal(480, contexts[1].Width); // 交互稳定后先给低成本真实帧。
        Assert.Equal(960, contexts[2].Width); // 用户请求精细质量时随后提升。
    }

    [Fact]
    public async Task 配置精度不足会在状态栏明确报告()
    {
        using var fixture = new DocumentFixture();
        var document = fixture.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("精度不足"), CancellationToken.None);

        document.PrecisionDigits = 32;
        document.Scale = "1e-24";

        Assert.Contains("配置精度不足", document.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 九宫格采用收藏恢复与续变都通过Document历史和配方工作()
    {
        using var fixture = new DocumentFixture();
        var document = fixture.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("变体闭环"), CancellationToken.None);
        var original = document.Artwork.ToVariationRecipe();

        await document.GenerateVariationsCommand.ExecuteAsync(null);

        Assert.Equal(9, document.VariationCandidates.Count);
        Assert.Equal(1, document.Artwork.Exploration.Generation);
        var selected = document.VariationCandidates[0];
        document.ToggleFavoriteCommand.Execute(selected);
        Assert.Single(document.Favorites);
        document.ApplyVariationCommand.Execute(selected);
        Assert.Equal(selected.Definition.Recipe, document.Artwork.ToVariationRecipe());
        Assert.NotEqual(original, document.Artwork.ToVariationRecipe());

        document.UndoCommand.Execute(null);
        Assert.Equal(original, document.Artwork.ToVariationRecipe());
        document.RestoreFavoriteCommand.Execute(document.Favorites[0]);
        Assert.Equal(selected.Definition.Recipe, document.Artwork.ToVariationRecipe());

        var snapshot = await document.CaptureSaveSnapshotAsync(CancellationToken.None);
        var restored = new ArtworkSnapshotCodec(new ArtworkValidator()).Decode(snapshot.Content);
        Assert.Single(restored.Exploration.Favorites);
        Assert.Equal(selected.Definition.Recipe, restored.Exploration.Favorites[0].Recipe);
    }

    [Fact]
    public async Task 取消九宫格不会污染当前作品或上一批候选()
    {
        var pipeline = new BlockingVariationPipeline();
        using var fixture = new DocumentFixture(pipeline);
        var document = fixture.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("取消变体"), CancellationToken.None);
        var before = document.Artwork;

        var explore = document.GenerateVariationsCommand.ExecuteAsync(null);
        await pipeline.VariationStarted.WaitAsync(TimeSpan.FromSeconds(5));
        document.CancelOperationCommand.Execute(null);
        await explore;

        Assert.Equal(before, document.Artwork);
        Assert.Empty(document.VariationCandidates);
        Assert.Contains("保持不变", document.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 递归树与Julia共用Document历史保存变体和导出编排状态()
    {
        using var firstFixture = new DocumentFixture();
        using var restoredFixture = new DocumentFixture();
        var document = firstFixture.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("路径作品"), CancellationToken.None);

        document.ApplyArtworkPresetCommand.Execute("verdant-growth");
        document.TreeDepth = 8;
        document.TreeBranches = 3;
        document.TreeBranchAngle = 30;
        document.TreeLengthDecay = 0.64;
        document.TreeRandomness = 0.25;
        document.TreeStrokeWidth = 6;
        var treeBeforePan = document.Artwork.RecursiveTree;
        document.PanViewport(50, 40, 600);
        await document.GenerateVariationsCommand.ExecuteAsync(null);

        Assert.True(document.IsRecursiveTreeGenerator);
        Assert.False(document.IsJuliaGenerator);
        Assert.Equal(treeBeforePan, document.Artwork.RecursiveTree);
        Assert.Equal(9, document.VariationCandidates.Count);
        Assert.All(document.Artwork.Exploration.Candidates, candidate =>
            Assert.Equal(FractalGeneratorKind.RecursiveTree, candidate.Recipe.GeneratorKind));

        var snapshot = await document.CaptureSaveSnapshotAsync(CancellationToken.None);
        var restored = restoredFixture.CreateDocument();
        await restored.InitializeAsync(
            new RestoreDocumentActivation("路径作品恢复", snapshot.Content),
            CancellationToken.None);

        Assert.Equal(document.Artwork.GeneratorKind, restored.Artwork.GeneratorKind);
        Assert.Equal(document.Artwork.RecursiveTree, restored.Artwork.RecursiveTree);
        Assert.Equal(document.Artwork.Julia, restored.Artwork.Julia);
        Assert.Equal(document.Artwork.Gradient, restored.Artwork.Gradient);
        Assert.Equal(document.Artwork.Exploration.Generation, restored.Artwork.Exploration.Generation);
        Assert.Equal(document.Artwork.Exploration.Candidates, restored.Artwork.Exploration.Candidates);
        Assert.True(restored.IsRecursiveTreeGenerator);
        Assert.False(restored.IsDirty);
        restored.UndoCommand.Execute(null);
        Assert.False(restored.CanRedo); // 恢复不会伪造当前进程的编辑历史。
    }

    [Fact]
    public async Task 生成器切换后只编辑当前时间逃逸定义且LSystem规则可自定义()
    {
        using var fixture = new DocumentFixture();
        using var document = fixture.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("G0005.1"), CancellationToken.None);

        document.SelectGeneratorCommand.Execute("Mandelbrot");
        document.CenterX = "-0.75";

        Assert.True(document.IsEscapeTimeFamily);
        Assert.True(document.IsMandelbrotGenerator);
        Assert.Equal("-7.5e-1", document.Artwork.Mandelbrot.CenterX);
        Assert.Equal("0", document.Artwork.Julia.CenterX);

        document.SelectGeneratorCommand.Execute("LSystem");
        document.LSystemAxiom = "F";
        document.LSystemRulesText = "F=F+F";
        document.LSystemIterations = 3;

        Assert.True(document.IsLSystemFamily);
        Assert.True(document.IsLSystemGenerator);
        Assert.Equal([new LSystemRuleDefinition('F', "F+F")], document.Artwork.LSystem.Rules);
        Assert.Contains("8 条线段", document.LSystemDiagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 图层命令参数路由MasterEffects与撤销重做形成同一作品历史()
    {
        using var fixture = new DocumentFixture();
        using var document = fixture.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("G0008"), CancellationToken.None);

        document.AddLayerCommand.Execute("Mandelbrot");
        Assert.Equal(2, document.Artwork.Layers.Count);
        Assert.True(document.IsMandelbrotGenerator);
        document.CenterX = "-0.75";
        document.SelectedLayerName = "遮罩源";
        document.SelectedLayerOpacity = 0.7;

        var juliaItem = document.LayerItems.Single(item => item.Id == "layer-1");
        document.SelectLayerCommand.Execute(juliaItem);
        document.ConstantReal = "-0.61";
        document.SelectedMaskSource = document.MaskSources.Single(option => option.Name == "遮罩源");
        document.MaskThreshold = 0.4;
        document.ToneEnabled = true;
        document.ToneBrightness = 0.15;

        var julia = Assert.IsType<FractalLayerDefinition>(document.SelectedLayer);
        Assert.Equal("-6.1e-1", julia.Julia.ConstantReal);
        Assert.Equal("layer-2", julia.Mask!.SourceLayerId);
        Assert.Equal(0.4, julia.Mask.Threshold);
        Assert.Equal(-0.75, ArbitraryDecimal.Parse(
            ArtworkLayerTree.FindFractal(document.Artwork.Layers, "layer-2")!.Mandelbrot.CenterX).ToDouble(), 12);
        Assert.True(document.IsDirty);
        Assert.True(document.ToneEnabled);
        Assert.True(document.CanUndo);

        document.UndoCommand.Execute(null);
        Assert.Equal(0, document.ToneBrightness);
        document.RedoCommand.Execute(null);
        Assert.Equal(0.15, document.ToneBrightness);
    }

    [Fact]
    public async Task 数学透镜会话不改变Dirty历史快照或预览指纹()
    {
        using var fixture = new DocumentFixture();
        using var document = fixture.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("G0010"), CancellationToken.None);
        var before = await document.CaptureSaveSnapshotAsync(CancellationToken.None);
        var fingerprint = document.LastPreviewFingerprint;

        await document.ToggleMathLensCommand.ExecuteAsync(null);
        document.NextMathLensFrameCommand.Execute(null);
        await document.SelectMathLensPointAsync(0.42, 0.58);
        document.CancelMathLensCommand.Execute(null);
        var after = await document.CaptureSaveSnapshotAsync(CancellationToken.None);

        Assert.True(document.MathLens.IsOpen);
        Assert.False(document.IsDirty);
        Assert.False(document.CanUndo);
        Assert.Equal(fingerprint, document.LastPreviewFingerprint);
        Assert.Equal(before.Content.Payload.GetRawText(), after.Content.Payload.GetRawText());
    }

    [Fact]
    public async Task 新建引导是会话态且恢复作品默认不显示()
    {
        using var firstFixture = new DocumentFixture();
        using var restoredFixture = new DocumentFixture();
        using var document = firstFixture.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("G0011 新建"), CancellationToken.None);
        var before = await document.CaptureSaveSnapshotAsync(CancellationToken.None);

        Assert.True(document.ShowGettingStarted);
        Assert.Equal(ArtworkWorkspacePhase.Ready, document.WorkspacePhase);
        document.DismissGettingStartedCommand.Execute(null);
        var after = await document.CaptureSaveSnapshotAsync(CancellationToken.None);

        Assert.False(document.ShowGettingStarted);
        Assert.False(document.IsDirty);
        Assert.Equal(before.Content.Payload.GetRawText(), after.Content.Payload.GetRawText());

        using var restored = restoredFixture.CreateDocument();
        await restored.InitializeAsync(
            new RestoreDocumentActivation("G0011 恢复", before.Content),
            CancellationToken.None);
        Assert.False(restored.ShowGettingStarted);
    }

    [Fact]
    public async Task 导出尺寸透明选项不进入作品Dirty历史或快照()
    {
        using var fixture = new DocumentFixture();
        using var document = fixture.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("G0011 导出"), CancellationToken.None);
        var before = await document.CaptureSaveSnapshotAsync(CancellationToken.None);

        document.ExportWidth = 3840;
        document.ExportTransparentBackground = true;

        Assert.Equal(3840, document.ExportWidth);
        Assert.Equal(2560, document.ExportHeight);
        Assert.Contains("透明", document.ExportSummary, StringComparison.Ordinal);
        Assert.Empty(document.ExportValidationMessage);
        Assert.False(document.IsDirty);
        Assert.False(document.CanUndo);
        var after = await document.CaptureSaveSnapshotAsync(CancellationToken.None);
        Assert.Equal(before.Content.Payload.GetRawText(), after.Content.Payload.GetRawText());

        document.ResetExportSizeCommand.Execute(null);
        Assert.Equal((1200, 800), (document.ExportWidth, document.ExportHeight));
    }

    [Fact]
    public async Task 导出预检失败不打开文件框且成功时传递已捕获计划()
    {
        using var fixture = new DocumentFixture();
        var exporter = new RecordingExporter();
        var dialog = new RecordingExportDialog("result.png");
        using var document = fixture.CreateDocument(exporter: exporter, exportDialog: dialog);
        await document.InitializeAsync(new NewDocumentActivation("G0011 预检"), CancellationToken.None);

        document.LockExportAspectRatio = false;
        document.ExportWidth = 8193;
        await document.ExportPngCommand.ExecuteAsync(null);
        Assert.Equal(0, dialog.Calls);
        Assert.Null(exporter.Plan);

        document.ExportWidth = 3840;
        document.ExportHeight = 2160;
        document.ExportTransparentBackground = true;
        await document.ExportPngCommand.ExecuteAsync(null);

        Assert.Equal(1, dialog.Calls);
        Assert.NotNull(exporter.Plan);
        Assert.Equal((3840, 2160), (exporter.Plan.Context.Width, exporter.Plan.Context.Height));
        Assert.True(exporter.Plan.Request.TransparentBackground);
    }

    [Fact]
    public async Task 已有成功预览后的失败保留指纹并可重试恢复()
    {
        var pipeline = new SwitchableFailurePipeline();
        using var fixture = new DocumentFixture(pipeline);
        using var document = fixture.CreateDocument();
        await document.InitializeAsync(new NewDocumentActivation("G0011 错误态"), CancellationToken.None);
        var fingerprint = document.LastPreviewFingerprint;

        pipeline.ShouldFail = true;
        await document.RenderPreviewNowAsync();

        Assert.Equal(ArtworkWorkspacePhase.Failed, document.WorkspacePhase);
        Assert.Equal(fingerprint, document.LastPreviewFingerprint);
        Assert.Contains("最后一张成功画面", document.WorkspaceMessage, StringComparison.Ordinal);

        pipeline.ShouldFail = false;
        await document.RetryPreviewCommand.ExecuteAsync(null);
        Assert.Equal(ArtworkWorkspacePhase.Ready, document.WorkspacePhase);
        Assert.Equal(fingerprint, document.LastPreviewFingerprint);
    }

    [Fact]
    public async Task 缺失能力恢复时不渲染且显式移除可撤销()
    {
        using var fixture = new DocumentFixture();
        using var document = fixture.CreateDocument();
        var validator = new ArtworkValidator();
        var codec = new ArtworkSnapshotCodec(validator);
        var source = ArtworkDefinition.CreateDefault();
        var unavailable = new UnavailableLayerDefinition(
            "future-layer", "未来图层", true, 1, LayerBlendMode.Normal,
            LayerTransformDefinition.Identity, null, "future.layer", 2, "{\"value\":7}");
        var blocked = source with { Layers = source.Layers.Append(unavailable).ToArray() };

        await document.InitializeAsync(
            new RestoreDocumentActivation("缺失能力", codec.Encode(blocked)),
            CancellationToken.None);

        Assert.Equal(0, fixture.Pipeline.CallCount);
        Assert.Equal(ArtworkWorkspacePhase.Blocked, document.WorkspacePhase);
        var issue = Assert.Single(document.CompatibilityReport.Issues);
        document.RemoveUnavailableCapabilityCommand.Execute(issue);
        await WaitUntilAsync(() => document.WorkspacePhase == ArtworkWorkspacePhase.Ready);

        Assert.True(document.CompatibilityReport.CanRender);
        Assert.True(document.IsDirty);
        Assert.True(document.CanUndo);

        document.UndoCommand.Execute(null);
        await WaitUntilAsync(() => document.WorkspacePhase == ArtworkWorkspacePhase.Blocked);
        Assert.False(document.CompatibilityReport.CanRender);
        var restoredUnavailable = Assert.IsType<UnavailableLayerDefinition>(document.Artwork.Layers[^1]);
        Assert.Equal(unavailable.OpaquePayload, restoredUnavailable.OpaquePayload);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class DocumentFixture : IDisposable
    {
        private readonly TestLifetime _lifetime = new();
        private readonly IArtworkRenderPipeline _pipeline;

        public DocumentFixture(IArtworkRenderPipeline? pipeline = null)
        {
            _pipeline = pipeline ?? new ImmediatePipeline();
        }

        public ImmediatePipeline Pipeline => Assert.IsType<ImmediatePipeline>(_pipeline);

        public FractalArtworkDocument CreateDocument(
            IImageLabArtEffectExportCoordinator? imageLabCoordinator = null,
            IImageLabExportDialog? imageLabExportDialog = null,
            IArtworkExporter? exporter = null,
            IArtworkExportDialog? exportDialog = null)
        {
            var validator = new ArtworkValidator();
            var generator = new VariationGenerator(validator);
            var lSystemValidator = new LSystemValidator();
            var kernels = new IAttractorFormulaKernel[] { new CliffordAttractorKernel(), new DeJongAttractorKernel() };
            var mathLens = new MathLensSession(new MathLensService([
                new EscapeTimeMathLensProvider(new LinearGradientMapper()),
                new PathMathLensProvider(
                    new RecursiveTreePathGenerator(),
                    new LSystemExpander(lSystemValidator),
                    new TurtlePathInterpreter()),
                new AttractorMathLensProvider(new StrangeAttractorPointGenerator(kernels), kernels)
            ]));
            return new FractalArtworkDocument(
                validator,
                new ArtworkSnapshotCodec(validator),
                _pipeline,
                new NullPreviewFactory(),
                exporter ?? new NullExporter(),
                exportDialog ?? new NullExportDialog(),
                new ArtworkHistory(),
                new ArtisticParameterMapper(),
                new VariationExplorer(generator, _pipeline),
                new ArtworkPresetCatalog(),
                _lifetime,
                imageLabCoordinator: imageLabCoordinator,
                imageLabExportDialog: imageLabExportDialog,
                mathLensSession: mathLens);
        }

        public void Dispose() => _lifetime.Dispose();
    }

    private sealed class ImmediatePipeline : IArtworkRenderPipeline
    {
        public int CallCount { get; private set; }

        public Task<ArtworkRenderResult> RenderAsync(ArtworkDefinition artwork, RenderContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(CreateResult(ControlledPipeline.CreateImage(artwork.Julia.ConstantReal)));
        }
    }

    private sealed class ControlledPipeline : IArtworkRenderPipeline
    {
        private int _callCount;

        public async Task<ArtworkRenderResult> RenderAsync(ArtworkDefinition artwork, RenderContext context, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _callCount);
            if (call > 1)
            {
                // 特意忽略取消，模拟底层库迟到返回；Document 仍必须在提交前拦截。
                await Task.Delay(ArbitraryDecimal.Parse(artwork.Julia.ConstantReal).ToDouble() == -0.5 ? 120 : 10);
            }

            return CreateResult(CreateImage(artwork.Julia.ConstantReal));
        }

        public static ImageSurface CreateImage(string constantRealText)
        {
            var constantReal = ArbitraryDecimal.Parse(constantRealText).ToDouble();
            var value = (byte)Math.Clamp((int)Math.Round((constantReal + 2) * 50), 0, 255);
            return new ImageSurface(1, 1, [value, 0, 0, 255]);
        }
    }

    private sealed class ProgressivePipeline : IArtworkRenderPipeline
    {
        private readonly object _sync = new();
        private readonly List<RenderContext> _contexts = [];
        private readonly List<(int Target, TaskCompletionSource Completion)> _waiters = [];

        public IReadOnlyList<RenderContext> Contexts
        {
            get
            {
                lock (_sync)
                {
                    return _contexts.ToArray();
                }
            }
        }

        public Task WaitForCallsAsync(int count)
        {
            lock (_sync)
            {
                if (_contexts.Count >= count)
                {
                    return Task.CompletedTask;
                }

                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((count, completion));
                return completion.Task;
            }
        }

        public Task<ArtworkRenderResult> RenderAsync(
            ArtworkDefinition artwork,
            RenderContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                _contexts.Add(context);
                foreach (var waiter in _waiters.Where(waiter => _contexts.Count >= waiter.Target).ToArray())
                {
                    waiter.Completion.TrySetResult();
                    _waiters.Remove(waiter);
                }
            }

            return Task.FromResult(CreateResult(ControlledPipeline.CreateImage(artwork.Julia.ConstantReal)));
        }
    }

    private sealed class BlockingVariationPipeline : IArtworkRenderPipeline
    {
        private int _calls;
        private readonly TaskCompletionSource _variationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task VariationStarted => _variationStarted.Task;

        public async Task<ArtworkRenderResult> RenderAsync(
            ArtworkDefinition artwork,
            RenderContext context,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                return CreateResult(ControlledPipeline.CreateImage(artwork.Julia.ConstantReal));
            }

            _variationStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("取消后不应到达此处。");
        }
    }

    private static ArtworkRenderResult CreateResult(ImageSurface image) => new(
        image,
        new ArtworkRenderExecutionSummary([], ["test"], 1));

    private sealed class NullPreviewFactory : IPreviewImageFactory
    {
        public Bitmap? Create(ImageSurface image, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    private sealed class NullExporter : IArtworkExporter
    {
        public Task ExportAsync(ArtworkExportPlan plan, string path, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class SwitchableFailurePipeline : IArtworkRenderPipeline
    {
        public bool ShouldFail { get; set; }

        public Task<ArtworkRenderResult> RenderAsync(
            ArtworkDefinition artwork,
            RenderContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ShouldFail
                ? Task.FromException<ArtworkRenderResult>(new InvalidOperationException("测试渲染器失败"))
                : Task.FromResult(CreateResult(ControlledPipeline.CreateImage(artwork.Julia.ConstantReal)));
        }
    }

    private sealed class NullExportDialog : IArtworkExportDialog
    {
        public Task<string?> PickPngPathAsync(string suggestedName, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);
    }

    private sealed class RecordingExporter : IArtworkExporter
    {
        public ArtworkExportPlan? Plan { get; private set; }

        public Task ExportAsync(ArtworkExportPlan plan, string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Plan = plan;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingExportDialog(string? path) : IArtworkExportDialog
    {
        public int Calls { get; private set; }

        public Task<string?> PickPngPathAsync(string suggestedName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(path);
        }
    }

    private sealed class TestLifetime : IDocumentLifetime, IDisposable
    {
        private readonly CancellationTokenSource _source = new();
        public CancellationToken ClosingToken => _source.Token;
        public bool IsClosing => _source.IsCancellationRequested;
        public void Dispose()
        {
            if (!_source.IsCancellationRequested)
            {
                _source.Cancel();
            }

            _source.Dispose();
        }
    }

    private sealed class UnavailableImageLabCoordinator : IImageLabArtEffectExportCoordinator
    {
        public int ExportCalls { get; private set; }
        public bool IsAvailable() => false;

        public Task<ImageLabExportResult> ExportAsync(
            ArtworkDefinition artwork, ImageLabEffectSettings effects, string outputPath,
            IProgress<int>? progress, CancellationToken cancellationToken)
        {
            ExportCalls++;
            throw new InvalidOperationException("不可调用");
        }
    }

    private sealed class RecordingImageLabDialog : IImageLabExportDialog
    {
        public int Calls { get; private set; }

        public Task<string?> PickOutputPathAsync(string suggestedName, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<string?>(null);
        }
    }
}
