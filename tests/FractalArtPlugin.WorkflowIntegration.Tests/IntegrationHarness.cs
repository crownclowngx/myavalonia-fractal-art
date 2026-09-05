using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Platform.Storage;
using FractalArtPlugin.Application.Workflow;
using FractalArtPlugin.Domain.Artwork;
using FractalArtPlugin.Plugin;
using ImageLabPlugin.Plugin;
using Microsoft.Extensions.DependencyInjection;
using MyAvaloniaManagement.PluginSdk;
using MyAvaloniaManagement.PluginSdk.UI;
using MyAvaloniaManagement.PluginSdk.Workflow;
using WorkflowStudio.Plugin;
using WorkflowStudio.Workflows;
using WorkflowStudio.Workflows.ArtWorkflow;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace FractalArtPlugin.WorkflowIntegration.Tests;

public sealed class PlatformFixture : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    public PlatformFixture()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() =>
        {
            try
            {
                AppBuilder.Configure<TestApplication>()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
                ready.SetResult();
                Avalonia.Threading.Dispatcher.UIThread.MainLoop(_stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception ex) { ready.TrySetException(ex); }
        })
        { IsBackground = true, Name = "G0013 headless UI" };
        if (OperatingSystem.IsWindows()) _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start(); ready.Task.GetAwaiter().GetResult();
    }
    public void Dispose() { _stop.Cancel(); _thread.Join(TimeSpan.FromSeconds(5)); _stop.Dispose(); }
    private sealed class TestApplication : Avalonia.Application
    {
        public override void Initialize() => Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
    }
}

/// <summary>
/// 本地 SDK 适配器使用真实 Module、描述符、Handler 和独立服务容器；只通过 SDK/BCL/JSON 交接。
/// 它验证应用级组合，不模拟真实 Host 的确认 UI、授权、卸载或发布，因此不能作为这些门禁的替代品。
/// </summary>
internal sealed class IntegrationHarness : IAsyncDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "g0013-integration", Guid.NewGuid().ToString("N"));
    internal string RecipePath => Path.Combine(Root, "source.fractal-workflow.json");
    internal string OutputRoot => Path.Combine(Root, "outputs");
    internal LocalGateway Gateway { get; } = new();
    internal TestLifetime Lifetime { get; } = new();
    internal RecordingRegistration Fractal { get; }
    internal RecordingRegistration ImageLab { get; }
    internal RecordingRegistration Studio { get; }
    internal IServiceScope StudioScope { get; }
    internal ArtWorkflowRecoverySession Recovery => StudioScope.ServiceProvider.GetRequiredService<ArtWorkflowRecoverySession>();
    internal ArtWorkflowDefinitionBuilder Builder => StudioScope.ServiceProvider.GetRequiredService<ArtWorkflowDefinitionBuilder>();
    internal IWorkflowRunner Runner => StudioScope.ServiceProvider.GetRequiredService<IWorkflowRunner>();
    internal WorkflowActionCatalogSnapshot Catalog => StudioScope.ServiceProvider.GetRequiredService<IWorkflowActionCatalogProjection>().Capture();

    internal IntegrationHarness()
    {
        Directory.CreateDirectory(OutputRoot);
        Fractal = Register(new FractalArtPluginModule(), ArtWorkflowDefinitionBuilder.FractalId);
        ImageLab = Register(new ImageLabPluginModule(), ArtWorkflowDefinitionBuilder.ImageLabId);
        Studio = Register(new WorkflowStudioModule(), "myavalonia.plugin.workflow-studio");
        StudioScope = Studio.Provider!.CreateScope();
    }

    private RecordingRegistration Register(IPluginModule module, string id)
    {
        var registration = new RecordingRegistration(id);
        module.Configure(registration);
        registration.Services.AddSingleton<IWorkflowActionGateway>(Gateway);
        registration.Services.AddSingleton<IDocumentLifetime>(Lifetime);
        registration.Services.AddSingleton<IPluginWindowInteraction, NullWindow>();
        registration.Provider = registration.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        foreach (var action in registration.Actions) Gateway.Actions.Add(action.Descriptor.Id.Value, (registration, action.Descriptor, action.Handler));
        return registration;
    }

    internal async Task<ArtWorkflowPlan> PrepareAsync(int count)
    {
        using var scope = Fractal.Provider!.CreateScope();
        var artwork = ArtworkDefinition.CreateDefault() with { Canvas = new(64, 64, new(0, 0, 0, 0)) };
        await scope.ServiceProvider.GetRequiredService<IWorkflowRecipeFiles>().ExportAsync(artwork, RecipePath, default);
        var plan = Builder.Create(Catalog, Enumerable.Repeat(RecipePath, count).ToArray(), OutputRoot, new());
        Recovery.Attach(plan);
        return plan;
    }

    public async ValueTask DisposeAsync()
    {
        // 只经生产者 Release 回收由本测试实际捕获的源；不枚举或递归删除 Workflow 公共根。
        Gateway.Fault = null;
        foreach (var source in Gateway.Sources.Values)
        {
            await using var run = Gateway.CreateRun();
            await run.InvokeAsync(new(new WorkflowActionId(ArtWorkflowDefinitionBuilder.Release),
                JsonSerializer.SerializeToElement(new { artifact = source })), null, default);
        }
        Lifetime.Close(); StudioScope.Dispose();
        Studio.Provider!.Dispose(); ImageLab.Provider!.Dispose(); Fractal.Provider!.Dispose(); Lifetime.Dispose();
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }
}

internal sealed class RecordingRegistration(string id) : IPluginRegistration, IWorkflowActionRegistration, IWorkbenchCommandRegistration
{
    public PluginId PluginId { get; } = new(id);
    public IServiceCollection Services { get; } = new ServiceCollection();
    internal ServiceProvider? Provider { get; set; }
    internal List<(WorkflowActionDescriptor Descriptor, Type Handler)> Actions { get; } = [];
    internal int ScopesCreated;
    internal int ScopesDisposed;
    public void UseLifecycle<T>() where T : class, IPluginLifecycle => Services.AddSingleton<T>();
    public void AddDocument<T, V>(DocumentDescriptor descriptor) where T : class, IPluginDocument where V : Control, new() => Services.AddScoped<T>();
    public void AddPersistableDocument<T, V>(DocumentDescriptor descriptor) where T : class, IPersistablePluginDocument where V : Control, new() => Services.AddScoped<T>();
    public void AddTool<T, V>(ToolDescriptor descriptor) where T : class where V : Control, new() => throw new InvalidOperationException("本用例不应贡献 Tool。");
    public void AddWorkflowAction<T>(WorkflowActionDescriptor descriptor) where T : class, IWorkflowActionHandler
    {
        Actions.Add((descriptor, typeof(T)));
        Services.AddScoped<T>(provider => { _ = provider.GetRequiredService<ScopeProbe>(); return ActivatorUtilities.CreateInstance<T>(provider); });
        if (Services.All(service => service.ServiceType != typeof(ScopeProbe))) Services.AddScoped(_ => new ScopeProbe(this));
    }
    public void UseWorkflowActionGateway() { }
    public void AddDocumentCommand(CommandDescriptor descriptor, DocumentTypeId targetDocumentTypeId) { }
    public void AddMenuCommandContribution(MenuCommandContributionDescriptor descriptor) { }
    public void AddKeyBindingContribution(KeyBindingContributionDescriptor descriptor) { }
    private sealed class ScopeProbe : IDisposable
    {
        private readonly RecordingRegistration _owner;
        public ScopeProbe(RecordingRegistration owner) { _owner = owner; owner.ScopesCreated++; }
        public void Dispose() => _owner.ScopesDisposed++;
    }
}

internal sealed class LocalGateway : IWorkflowActionGateway
{
    internal Dictionary<string, (RecordingRegistration Registration, WorkflowActionDescriptor Descriptor, Type Handler)> Actions { get; } = [];
    internal Dictionary<Guid, JsonElement> Sources { get; } = [];
    internal List<WorkflowActionInvocationRequest> Requests { get; } = [];
    internal Func<WorkflowActionInvocationRequest, WorkflowActionInvocationResult?>? Fault { get; set; }
    internal Action<WorkflowActionInvocationRequest, JsonElement>? AfterInvoke { get; set; }
    internal Func<WorkflowActionInvocationRequest, JsonElement, WorkflowActionInvocationResult?>? TransformOutput { get; set; }
    internal int RunsCreated;
    internal int RunsDisposed;
    public IReadOnlyList<WorkflowActionDescriptor> GetAvailableActions() => Actions.Values.Select(item => item.Descriptor).ToArray();
    public IWorkflowActionRun CreateRun() { RunsCreated++; return new Run(this); }
    internal static WorkflowActionInvocationResult Failed(string code = "test.interrupted") =>
        new(Guid.NewGuid(), WorkflowActionInvocationStatus.Failed, null, new(code, "受控中断"));
    private sealed class Run(LocalGateway owner) : IWorkflowActionRun
    {
        private bool _disposed;
        public async Task<WorkflowActionInvocationResult> InvokeAsync(WorkflowActionInvocationRequest request,
            IProgress<WorkflowActionProgress>? progress, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            owner.Requests.Add(request);
            if (owner.Fault?.Invoke(request) is { } fault) return fault;
            if (!owner.Actions.TryGetValue(request.ActionId.Value, out var action)) return Failed("action.missing");
            var id = Guid.NewGuid();
            try
            {
                var schema = new WorkflowSchemaValidator();
                Assert.True(schema.ValidateInstance(action.Descriptor.InputSchema, request.Arguments, 256 * 1024).IsValid);
                using var scope = action.Registration.Provider!.CreateScope();
                var handler = (IWorkflowActionHandler)scope.ServiceProvider.GetRequiredService(action.Handler);
                var output = await handler.InvokeAsync(request.Arguments,
                    new(id, new PluginId("myavalonia.plugin.workflow-studio"), progress ?? new QuietProgress()), cancellationToken);
                Assert.True(schema.ValidateInstance(action.Descriptor.OutputSchema, output, 1024 * 1024).IsValid);
                if (request.ActionId.Value == ArtWorkflowDefinitionBuilder.Render) Capture(output.GetProperty("artifact"));
                if (request.ActionId.Value == ArtWorkflowDefinitionBuilder.Batch)
                    foreach (var item in output.GetProperty("results").EnumerateArray()) Capture(item.GetProperty("artifact"));
                owner.AfterInvoke?.Invoke(request, output);
                if (owner.TransformOutput?.Invoke(request, output) is { } transformed) return transformed;
                return new(id, WorkflowActionInvocationStatus.Succeeded, output, null);
            }
            catch (OperationCanceledException) { return new(id, WorkflowActionInvocationStatus.Cancelled, null, null); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or JsonException)
            { return Failed("provider.rejected"); }
        }
        private void Capture(JsonElement source) => owner.Sources[source.GetProperty("producerOperationId").GetGuid()] = source.Clone();
        public ValueTask DisposeAsync() { if (!_disposed) { _disposed = true; owner.RunsDisposed++; } return ValueTask.CompletedTask; }
    }
    private sealed class QuietProgress : IProgress<WorkflowActionProgress> { public void Report(WorkflowActionProgress value) { } }
}

internal sealed class TestLifetime : IDocumentLifetime, IDisposable
{
    private readonly CancellationTokenSource _closing = new();
    public CancellationToken ClosingToken => _closing.Token;
    public bool IsClosing => _closing.IsCancellationRequested;
    public void Close() => _closing.Cancel();
    public void Dispose() => _closing.Dispose();
}
internal sealed class NullWindow : IPluginWindowInteraction
{
    public Task<IReadOnlyList<string>> PickOpenFilesAsync(FilePickerOpenOptions options, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);
    public Task<string?> PickSaveFileAsync(FilePickerSaveOptions options, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    public Task<bool> TrySetClipboardTextAsync(string text, CancellationToken cancellationToken) => Task.FromResult(false);
}
