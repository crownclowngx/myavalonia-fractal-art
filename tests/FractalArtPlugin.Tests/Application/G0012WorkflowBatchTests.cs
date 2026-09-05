using System.Security.Cryptography;
using System.Text.Json;
using FractalArtPlugin.Application.Workflow;
using FractalArtPlugin.Infrastructure;
using FractalArtPlugin.Infrastructure.Workflow;
using MyAvaloniaManagement.PluginSdk;
using MyAvaloniaManagement.PluginSdk.Workflow;
using Xunit;

namespace FractalArtPlugin.Tests;

public sealed class G0012WorkflowBatchTests
{
    [Fact]
    public void 三个描述符通过共享协议校验且批结果可以ForEach引用旧Release()
    {
        var validator = new WorkflowSchemaValidator();
        var batch = ExportArtworkBatchWorkflowAction.CreateDescriptor();
        var render = FractalWorkflowActions.CreateRenderDescriptor();
        var release = FractalWorkflowActions.CreateReleaseDescriptor();
        foreach (var descriptor in new[] { batch, render, release })
            Assert.True(validator.ValidateDescriptor(descriptor).IsValid);
        Assert.Equal("myavalonia.plugin.fractal.art.workflow.export-artwork-batch", batch.Id.Value);
        Assert.Equal(render.Risks, batch.Risks);
        Assert.Equal(WorkflowActionConfirmationPolicy.OncePerRun, batch.ConfirmationPolicy);
        var result = batch.OutputSchema.GetProperty("properties").GetProperty("results").GetProperty("items");
        var source = result.GetProperty("properties").GetProperty("artifact");
        var target = release.InputSchema.GetProperty("properties").GetProperty("artifact");
        Assert.True(new WorkflowReferenceTypeSystem().ValidateAssignable(source, target).IsValid);
        Assert.True(WorkflowReferencePath.ResolveGuaranteedSchemaPath(batch.OutputSchema, ["results"]).Succeeded);
        Assert.True(WorkflowReferencePath.ResolveGuaranteedSchemaPath(result, ["artifact"]).Succeeded);
        Assert.True(JsonElement.DeepEquals(render.OutputSchema.GetProperty("properties").GetProperty("artifact"), source));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"items\":[]}")]
    [InlineData("{\"items\":null}")]
    [InlineData("{\"items\":[{}]}")]
    [InlineData("{\"items\":[{\"itemId\":null,\"recipePath\":\"x\"}]}")]
    [InlineData("{\"items\":[{\"itemId\":\"a\",\"recipePath\":\"x\",\"extra\":true}]}")]
    [InlineData("{\"items\":[{\"itemId\":\"a\",\"itemId\":\"b\",\"recipePath\":\"x\"}]}")]
    [InlineData("{\"items\":[],\"items\":[]}")]
    [InlineData("{\"items\":[],\"callerId\":\"fake\"}")]
    public void 参数拒绝错误形状未知重复或缺失字段(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<InvalidDataException>(() => ExportArtworkBatchWorkflowAction.Parse(document.RootElement));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("too-many")]
    [InlineData("duplicate")]
    [InlineData("blank")]
    [InlineData("long-id")]
    [InlineData("relative")]
    public async Task 非法批请求不会读取或生成产物(string scenario)
    {
        var reader = new Reader();
        var store = new Store();
        var items = Items(scenario == "too-many" ? 17 : 2);
        if (scenario == "empty") items = [];
        if (scenario == "duplicate") items[1] = items[0];
        if (scenario == "blank") items[0] = items[0] with { ItemId = " " };
        if (scenario == "long-id") items[0] = items[0] with { ItemId = new string('a', 65) };
        if (scenario == "relative") items[0] = items[0] with { RecipePath = "relative.json" };
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(reader, store).ExportAsync(items,
            Guid.NewGuid(), new BatchProgress(), CancellationToken.None));
        Assert.Equal(0, reader.Reads);
        Assert.Empty(store.Created);
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("pixels")]
    [InlineData("edge")]
    [InlineData("broken")]
    public async Task 整批预检失败时即使前项合法也不会写入(string scenario)
    {
        var reader = new Reader
        {
            Read = index => scenario switch
            {
                "bytes" => new(SmallArtwork(), WorkflowBatchExporter.MaximumRecipeBytes),
                "pixels" => new(SmallArtwork() with { Canvas = new(4096, 4096, new(0, 0, 0)) }, 1),
                "edge" when index == 2 => new(SmallArtwork() with { Canvas = new(4097, 16, new(0, 0, 0)) }, 1),
                "broken" when index == 2 => throw new InvalidDataException("模拟损坏配方"),
                _ => new(SmallArtwork(), 1)
            }
        };
        var store = new Store();
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(reader, store).ExportAsync(Items(5),
            Guid.NewGuid(), new BatchProgress(), CancellationToken.None));
        Assert.Empty(store.Created);
    }

    [Fact]
    public async Task 成功输出保持顺序独立身份及真实Schema且进度单调()
    {
        var reader = new Reader();
        var store = new Store { BeforeCreate = () => Assert.Equal(3, reader.Reads) };
        var progress = new ActionProgress();
        var invocation = Guid.NewGuid();
        var handler = new ExportArtworkBatchWorkflowActionHandler(Service(reader, store));
        var input = Arguments(Items(3));
        var output = await handler.InvokeAsync(input, Context(progress, invocation), CancellationToken.None);
        var schema = new WorkflowSchemaValidator();
        var descriptor = ExportArtworkBatchWorkflowAction.CreateDescriptor();
        Assert.True(schema.ValidateInstance(descriptor.InputSchema, input, WorkflowSchemaProfile.MaximumInputBytes).IsValid);
        Assert.True(schema.ValidateInstance(descriptor.OutputSchema, output, WorkflowSchemaProfile.MaximumOutputBytes).IsValid);
        Assert.Equal(new[] { "item-0", "item-1", "item-2" }, output.GetProperty("results").EnumerateArray()
            .Select(item => item.GetProperty("itemId").GetString()));
        Assert.Equal(3, store.Created.Select(item => item.ProducerOperationId).Distinct().Count());
        Assert.All(store.Origins, origin => Assert.Equal(invocation, origin!.InvocationId));
        Assert.Empty(store.Released);
        Assert.Equal(progress.Values.Select(value => value.Percent).Order(), progress.Values.Select(value => value.Percent));
        Assert.Equal(100, progress.Values.Last().Percent);
    }

    [Fact]
    public async Task 达到批次数和配方总字节预算边界可以成功()
    {
        var reader = new Reader { Read = _ => new(SmallArtwork(), 1024 * 1024) };
        var store = new Store();
        await using var pending = await Service(reader, store).ExportAsync(Items(16), Guid.NewGuid(), new BatchProgress(), CancellationToken.None);
        Assert.Equal(16, pending.Results.Count);
        Assert.Equal(1024 * 1024, reader.Limits.Last());
        // 不 Commit 的调用者仍拥有回滚责任，await using 将回收全部产物。
    }

    [Theory]
    [InlineData("before")]
    [InlineData("reading")]
    [InlineData("rendering")]
    [InlineData("committing")]
    [InlineData("succeeded")]
    public async Task 各阶段取消都不会移交产物(string stage)
    {
        using var cancellation = new CancellationTokenSource();
        var reader = new Reader();
        var store = new Store();
        if (stage == "before") cancellation.Cancel();
        if (stage == "reading") reader.AfterRead = cancellation.Cancel;
        if (stage == "rendering") store.AfterCreate = cancellation.Cancel;
        var progress = new ActionProgress { OnReport = value => { if (value.Stage == stage) cancellation.Cancel(); } };
        var handler = new ExportArtworkBatchWorkflowActionHandler(Service(reader, store));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await handler.InvokeAsync(Arguments(Items(3)), Context(progress), cancellation.Token));
        Assert.Equal(store.Created.Count, store.Released.Count);
        Assert.All(store.ReleaseTokens, token => Assert.False(token.CanBeCanceled));
        if (stage == "before") Assert.Equal(0, reader.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 第N项失败继续回滚全部前项且不掩盖原异常(bool throwOnRelease)
    {
        var failure = new IOException("第三项渲染失败");
        var store = new Store { FailAt = 3, Failure = failure, ThrowOnRelease = throwOnRelease };
        var caught = await Assert.ThrowsAsync<IOException>(() => Service(new Reader(), store).ExportAsync(Items(4),
            Guid.NewGuid(), new BatchProgress(), CancellationToken.None));
        Assert.Same(failure, caught);
        Assert.Equal(2, store.Released.Count);
        Assert.Equal(store.Created.AsEnumerable().Reverse(), store.Released);
    }

    [Fact]
    public async Task 序列化预算超限时仍回滚全部产物()
    {
        var store = new Store { ArtifactPath = new string('x', 600_000) };
        var handler = new ExportArtworkBatchWorkflowActionHandler(Service(new Reader(), store));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await handler.InvokeAsync(Arguments(Items(2)), Context(new()), CancellationToken.None));
        Assert.Equal(2, store.Released.Count);
    }

    [Fact]
    public async Task 单张Render在迟到取消后回滚并保持旧契约()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new Store { AfterCreate = cancellation.Cancel };
        var handler = new RenderArtworkFileWorkflowActionHandler(new Reader(), store);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await handler.InvokeAsync(
            JsonSerializer.SerializeToElement(new { recipePath = Items(1)[0].RecipePath }), Context(new()), cancellation.Token));
        Assert.Single(store.Released);
    }

    [Fact]
    public async Task 缺失效果在任何产物创建前失败并保留输入()
    {
        var artwork = SmallArtwork() with
        {
            MasterEffects = new(1, [new UnavailableEffectDefinition("future.effect", 9, true, "{\"value\":1}")])
        };
        var store = new Store();
        var reader = new Reader { Read = index => new(index == 2 ? artwork : SmallArtwork(), 1) };
        await Assert.ThrowsAsync<NotSupportedException>(() => Service(reader, store).ExportAsync(Items(2),
            Guid.NewGuid(), new BatchProgress(), CancellationToken.None));
        Assert.Empty(store.Created);
        Assert.IsType<UnavailableEffectDefinition>(Assert.Single(artwork.MasterEffects.Effects));
    }

    [Fact]
    public async Task 总像素预算等号边界可接受而不执行真实大图渲染()
    {
        var reader = new Reader { Read = _ => new(SmallArtwork() with { Canvas = new(4096, 4096, new(0, 0, 0)) }, 1) };
        var store = new Store();
        await using var pending = await Service(reader, store).ExportAsync(Items(4), Guid.NewGuid(), new BatchProgress(), CancellationToken.None);
        Assert.Equal(4, pending.Results.Count);
    }

    [Fact]
    public void Provider及批次服务仅依赖窄端口而不依赖Gateway或Document()
    {
        Assert.Equal(new[] { typeof(IWorkflowBatchExporter) }, typeof(ExportArtworkBatchWorkflowActionHandler)
            .GetConstructors().Single().GetParameters().Select(parameter => parameter.ParameterType));
        Assert.Equal(new[] { typeof(IWorkflowBoundedRecipeReader), typeof(IArtworkExportPlanner), typeof(IFractalWorkflowArtifactStore) },
            typeof(WorkflowBatchExporter).GetConstructors().Single().GetParameters().Select(parameter => parameter.ParameterType));
    }

    internal static ArtworkDefinition SmallArtwork() => ArtworkDefinition.CreateDefault() with
    {
        Canvas = new(96, 64, new RgbaColor(1, 2, 3, 0))
    };

    internal static IArtworkExportPlanner Planner()
    {
        var validator = new ArtworkValidator();
        return new ArtworkExportPlanner(validator, validator);
    }

    private static WorkflowBatchExporter Service(Reader reader, Store store) => new(reader, Planner(), store);
    internal static WorkflowBatchItem[] Items(int count) => Enumerable.Range(0, count)
        .Select(index => new WorkflowBatchItem($"item-{index}", Path.Combine(Path.GetTempPath(), $"recipe-{index}.json"))).ToArray();
    internal static JsonElement Arguments(IEnumerable<WorkflowBatchItem> items) => JsonSerializer.SerializeToElement(new
    {
        items = items.Select(item => new { itemId = item.ItemId, recipePath = item.RecipePath }).ToArray()
    });
    internal static WorkflowActionContext Context(ActionProgress progress, Guid? invocation = null) =>
        new(invocation ?? Guid.NewGuid(), new PluginId("myavalonia.plugin.workflow.studio"), progress);

    private sealed class Reader : IWorkflowBoundedRecipeReader, IWorkflowRecipeFiles
    {
        internal int Reads { get; private set; }
        internal List<int> Limits { get; } = [];
        internal Func<int, WorkflowRecipeReadResult> Read { get; init; } = _ => new(SmallArtwork(), 1);
        internal Action? AfterRead { get; set; }
        public Task<WorkflowRecipeReadResult> ReadBoundedAsync(string path, int maximumBytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Limits.Add(maximumBytes);
            var result = Read(++Reads);
            AfterRead?.Invoke();
            return Task.FromResult(result);
        }
        public async Task<ArtworkDefinition> ReadAsync(string path, CancellationToken cancellationToken) =>
            (await ReadBoundedAsync(path, int.MaxValue, cancellationToken)).Artwork;
        public Task ExportAsync(ArtworkDefinition artwork, string path, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Store : IFractalWorkflowArtifactStore
    {
        internal List<WorkflowFileArtifact> Created { get; } = [];
        internal List<WorkflowFileArtifact> Released { get; } = [];
        internal List<CancellationToken> ReleaseTokens { get; } = [];
        internal List<WorkflowArtifactOrigin?> Origins { get; } = [];
        internal Action? BeforeCreate { get; init; }
        internal Action? AfterCreate { get; set; }
        internal int FailAt { get; init; }
        internal Exception Failure { get; init; } = new IOException();
        internal bool ThrowOnRelease { get; init; }
        internal string? ArtifactPath { get; init; }
        public Task<WorkflowFileArtifact> CreateAsync(ArtworkDefinition artwork, Guid operationId, string lifetime,
            CancellationToken cancellationToken, WorkflowArtifactOrigin? origin = null)
        {
            BeforeCreate?.Invoke();
            if (Created.Count + 1 == FailAt) throw Failure;
            var artifact = new WorkflowFileArtifact(FractalWorkflowFileArtifactContract.Name, 1,
                FractalWorkflowFileArtifactContract.PluginId, operationId, lifetime,
                ArtifactPath ?? Path.Combine(Path.GetTempPath(), operationId.ToString("D"), "source.png"), "image/png", 8, new string('A', 64));
            Created.Add(artifact);
            Origins.Add(origin);
            AfterCreate?.Invoke();
            return Task.FromResult(artifact);
        }
        public Task<ArtifactReleaseResult> ReleaseAsync(WorkflowFileArtifact artifact, bool allowTransient, CancellationToken cancellationToken)
        {
            Released.Add(artifact);
            ReleaseTokens.Add(cancellationToken);
            if (ThrowOnRelease) throw new IOException("模拟回滚异常");
            return Task.FromResult(new ArtifactReleaseResult(false, "cleanup_deferred"));
        }
        public Task CleanupExpiredAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    internal sealed class ActionProgress : IProgress<WorkflowActionProgress>
    {
        internal List<WorkflowActionProgress> Values { get; } = [];
        internal Action<WorkflowActionProgress>? OnReport { get; init; }
        public void Report(WorkflowActionProgress value) { Values.Add(value); OnReport?.Invoke(value); }
    }
    private sealed class BatchProgress : IProgress<WorkflowBatchProgress>
    {
        public void Report(WorkflowBatchProgress value) { }
    }
}
