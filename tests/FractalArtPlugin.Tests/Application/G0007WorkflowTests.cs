using System.Text;
using System.Text.Json;
using FractalArtPlugin.Application;
using FractalArtPlugin.Application.Workflow;
using FractalArtPlugin.Infrastructure.Workflow;
using MyAvaloniaManagement.PluginSdk;
using Xunit;

namespace FractalArtPlugin.Tests;

public sealed class G0007WorkflowTests
{
    [Fact]
    public void 配方外层v1嵌入ArtworkV7多层作品且不丢失状态()
    {
        var codec = CreateRecipeCodec();
        var validator = new ArtworkValidator();
        var editor = new ArtworkLayerEditor(validator);
        var expected = editor.AddFractal(
            ArtworkDefinition.CreateDefault() with { Seed = 987654321 },
            FractalGeneratorKind.Mandelbrot) with
        {
            MasterEffects = new EffectChainDefinition(1,
            [
                new ToneEffectDefinition(true, 0.1, 0.2, 1.1),
                new BloomEffectDefinition(false, 0.72, 2.4, 0.8)
            ])
        };

        var actual = codec.Decode(codec.Encode(expected));

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"artworkSchemaVersion\":1,\"artwork\":{}}")]
    [InlineData("{\"schemaVersion\":1,\"artworkSchemaVersion\":1,\"artwork\":{},\"extra\":true}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"artworkSchemaVersion\":1,\"artwork\":{}}")]
    [InlineData("{\"schemaVersion\":1,\"artworkSchemaVersion\":1}")]
    public void 配方拒绝未知版本未知字段重复字段和缺失字段(string json)
    {
        Assert.ThrowsAny<Exception>(() => CreateRecipeCodec().Decode(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public async Task Artifact创建后具有所有权标记摘要且释放幂等()
    {
        var operationId = Guid.NewGuid();
        var store = new FractalWorkflowArtifactStore(new StubExporter(), CreateExportPlanner());
        WorkflowFileArtifact? artifact = null;
        try
        {
            artifact = await store.CreateAsync(
                ArtworkDefinition.CreateDefault(), operationId,
                FractalWorkflowFileArtifactContract.RunLifetime, CancellationToken.None);

            Assert.True(Path.IsPathFullyQualified(artifact.Path));
            Assert.True(File.Exists(artifact.Path));
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(artifact.Path)!, ".owner.json")));
            Assert.Equal(8, artifact.ByteLength);
            Assert.Equal(64, artifact.Sha256.Length);
            Assert.Equal(artifact.Sha256.ToUpperInvariant(), artifact.Sha256);

            Assert.True((await store.ReleaseAsync(artifact, false, CancellationToken.None)).Released);
            Assert.True((await store.ReleaseAsync(artifact, false, CancellationToken.None)).Released);
        }
        finally
        {
            if (artifact is not null)
            {
                _ = await store.ReleaseAsync(artifact, true, CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task Release拒绝伪造路径和transient责任越界()
    {
        var operationId = Guid.NewGuid();
        var store = new FractalWorkflowArtifactStore(new StubExporter(), CreateExportPlanner());
        var artifact = await store.CreateAsync(
            ArtworkDefinition.CreateDefault(), operationId,
            FractalWorkflowFileArtifactContract.TransientLifetime, CancellationToken.None);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.ReleaseAsync(artifact, allowTransient: false, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.ReleaseAsync(artifact with { Path = Path.GetTempFileName() }, true, CancellationToken.None));
        }
        finally
        {
            _ = await store.ReleaseAsync(artifact, true, CancellationToken.None);
        }
    }

    [Fact]
    public async Task ImageLab缺失时协调器在创建Artifact之前失败()
    {
        var store = new RecordingArtifactStore();
        var coordinator = new ImageLabArtEffectExportCoordinator(store, new RecordingImageLabClient(false));

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ExportAsync(
            ArtworkDefinition.CreateDefault(), ImageLabEffectSettings.Default,
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png"), null, CancellationToken.None));

        Assert.Equal(0, store.CreateCount);
        Assert.Equal(0, store.ReleaseCount);
    }

    [Fact]
    public async Task ImageLab失败时协调器仍在finally释放transientArtifact()
    {
        var store = new RecordingArtifactStore();
        var client = new RecordingImageLabClient(true) { Failure = new IOException("模拟失败") };
        var coordinator = new ImageLabArtEffectExportCoordinator(store, client);

        await Assert.ThrowsAsync<IOException>(() => coordinator.ExportAsync(
            ArtworkDefinition.CreateDefault(), ImageLabEffectSettings.Default,
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png"), null, CancellationToken.None));

        Assert.Equal(1, store.CreateCount);
        Assert.Equal(1, store.ReleaseCount);
        Assert.True(store.AllowTransientOnRelease);
    }

    [Fact]
    public async Task ActionClient等Run完全释放并只接受ImageLab持久Artifact()
    {
        var outputPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png"));
        var run = new RecordingRun(outputPath);
        var gateway = new RecordingGateway(run);
        var client = new ImageLabActionClient(gateway);

        var result = await client.ApplyAsync(CreateSourceArtifact(), ImageLabEffectSettings.Default,
            outputPath, null, CancellationToken.None);

        Assert.Equal(outputPath, result.OutputPath);
        Assert.True(run.InvokeCompleted);
        Assert.True(run.Disposed);
        Assert.Equal(ImageLabActionClient.ActionId, run.Request!.ActionId);
        Assert.Equal(outputPath, run.Request.Arguments.GetProperty("outputPath").GetString());
    }

    [Fact]
    public void Provider元数据固定风险确认策略与ActionId()
    {
        var render = FractalWorkflowActions.CreateRenderDescriptor();
        var release = FractalWorkflowActions.CreateReleaseDescriptor();

        Assert.Equal("myavalonia.plugin.fractal.art.workflow.render-artwork-file", render.Id.Value);
        Assert.Equal(WorkflowActionConfirmationPolicy.OncePerRun, render.ConfirmationPolicy);
        Assert.True(render.Risks.HasFlag(WorkflowActionRiskFlags.LongRunning));
        Assert.Equal("myavalonia.plugin.fractal.art.workflow.release-artifact", release.Id.Value);
        Assert.Equal(WorkflowActionRiskFlags.DeletesLocalFiles, release.Risks);
        Assert.Equal(WorkflowActionConfirmationPolicy.EveryInvocation, release.ConfirmationPolicy);
    }

    private static WorkflowRecipeCodec CreateRecipeCodec() =>
        new(new ArtworkSnapshotCodec(new ArtworkValidator()));

    private static WorkflowFileArtifact CreateSourceArtifact() => new(
        FractalWorkflowFileArtifactContract.Name,
        FractalWorkflowFileArtifactContract.Version,
        FractalWorkflowFileArtifactContract.PluginId,
        Guid.NewGuid(),
        FractalWorkflowFileArtifactContract.TransientLifetime,
        Path.Combine(Path.GetTempPath(), "source.png"),
        FractalWorkflowFileArtifactContract.PngMediaType,
        8,
        new string('A', 64));

    private sealed class StubExporter : IArtworkExporter
    {
        public Task ExportAsync(ArtworkExportPlan plan, string path, CancellationToken cancellationToken) =>
            File.WriteAllBytesAsync(path, [137, 80, 78, 71, 13, 10, 26, 10], cancellationToken);
    }

    private static IArtworkExportPlanner CreateExportPlanner()
    {
        var validator = new ArtworkValidator();
        return new ArtworkExportPlanner(validator, validator);
    }

    private sealed class RecordingArtifactStore : IFractalWorkflowArtifactStore
    {
        public int CreateCount { get; private set; }
        public int ReleaseCount { get; private set; }
        public bool AllowTransientOnRelease { get; private set; }

        public Task<WorkflowFileArtifact> CreateAsync(
            ArtworkDefinition artwork, Guid operationId, string lifetime, CancellationToken cancellationToken,
            WorkflowArtifactOrigin? origin = null)
        {
            CreateCount++;
            return Task.FromResult(CreateSourceArtifact() with
            {
                ProducerOperationId = operationId,
                Lifetime = lifetime,
            });
        }

        public Task<ArtifactReleaseResult> ReleaseAsync(
            WorkflowFileArtifact artifact, bool allowTransient, CancellationToken cancellationToken)
        {
            ReleaseCount++;
            AllowTransientOnRelease = allowTransient;
            return Task.FromResult(new ArtifactReleaseResult(true, null));
        }

        public Task CleanupExpiredAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingImageLabClient(bool available) : IImageLabActionClient
    {
        public Exception? Failure { get; init; }
        public bool IsAvailable() => available;

        public Task<ImageLabExportResult> ApplyAsync(
            WorkflowFileArtifact source, ImageLabEffectSettings effects, string outputPath,
            IProgress<int>? progress, CancellationToken cancellationToken) => Failure is null
            ? Task.FromResult(new ImageLabExportResult(outputPath, 1, new string('A', 64)))
            : Task.FromException<ImageLabExportResult>(Failure);
    }

    private sealed class RecordingGateway(RecordingRun run) : IWorkflowActionGateway
    {
        public IReadOnlyList<WorkflowActionDescriptor> GetAvailableActions() =>
            [CreateForeignDescriptor(ImageLabActionClient.ActionId)];

        public IWorkflowActionRun CreateRun() => run;
    }

    private sealed class RecordingRun(string outputPath) : IWorkflowActionRun
    {
        public WorkflowActionInvocationRequest? Request { get; private set; }
        public bool InvokeCompleted { get; private set; }
        public bool Disposed { get; private set; }

        public Task<WorkflowActionInvocationResult> InvokeAsync(
            WorkflowActionInvocationRequest request, IProgress<WorkflowActionProgress>? progress,
            CancellationToken cancellationToken)
        {
            Request = request;
            InvokeCompleted = true;
            var output = JsonSerializer.SerializeToElement(new
            {
                artifact = new
                {
                    contract = FractalWorkflowFileArtifactContract.Name,
                    version = 1,
                    producerPluginId = "myavalonia.plugin.image.lab",
                    producerOperationId = Guid.NewGuid(),
                    lifetime = FractalWorkflowFileArtifactContract.PersistentLifetime,
                    path = outputPath,
                    mediaType = "image/png",
                    byteLength = 42,
                    sha256 = new string('B', 64),
                }
            });
            return Task.FromResult(new WorkflowActionInvocationResult(
                Guid.NewGuid(), WorkflowActionInvocationStatus.Succeeded, output, null));
        }

        public ValueTask DisposeAsync()
        {
            Assert.True(InvokeCompleted);
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private static WorkflowActionDescriptor CreateForeignDescriptor(WorkflowActionId id)
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        return new WorkflowActionDescriptor(id, "ImageLab", "ImageLab", schema.RootElement,
            schema.RootElement, WorkflowActionRiskFlags.None, WorkflowActionConfirmationPolicy.OncePerRun);
    }
}
