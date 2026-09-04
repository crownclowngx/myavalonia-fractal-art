using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using MyAvaloniaManagement.PluginSdk;
using MyAvaloniaManagement.PluginSdk.UI;
using FractalArtPlugin.Constants;
using FractalArtPlugin.Features.Artwork;
using FractalArtPlugin.Plugin;
using Xunit;

namespace FractalArtPlugin.Tests;

public sealed class PluginCompositionTests
{
    [Fact]
    public void Module只登记一个可持久化作品且不登记Tool命令或快捷键()
    {
        var registration = new CapturingRegistration();

        new FractalArtPluginModule().Configure(registration);

        var document = Assert.Single(registration.PersistableDocuments);
        Assert.Equal(PluginIds.FractalArtworkDocument, document.Descriptor.DocumentTypeId);
        Assert.Equal("分形作品", document.Descriptor.DisplayName);
        Assert.Equal(typeof(FractalArtworkDocument), document.Model);
        Assert.Equal(typeof(FractalArtworkView), document.View);
        Assert.Empty(registration.Documents);
        Assert.Empty(registration.Tools);
        Assert.Empty(registration.Commands);
        Assert.Empty(registration.MenuContributions);
        Assert.Empty(registration.KeyBindings);
    }

    [Fact]
    public void 稳定插件与Document身份保持冻结值()
    {
        Assert.Equal("myavalonia.plugin.fractal.art", PluginIds.Plugin.Value);
        Assert.Equal("myavalonia.plugin.fractal.art.document.main", PluginIds.FractalArtworkDocument.Value);
    }

    [Fact]
    public void 公共组合入口可以在严格Scope验证下构造独立Document()
    {
        var registration = new CapturingRegistration();
        new FractalArtPluginModule().Configure(registration);
        registration.Services.AddSingleton<IPluginWindowInteraction, NullWindowInteraction>();
        registration.Services.AddScoped<IDocumentLifetime, TestLifetime>();
        using var provider = registration.Services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();

        var first = firstScope.ServiceProvider.GetRequiredService<FractalArtworkDocument>();
        var second = secondScope.ServiceProvider.GetRequiredService<FractalArtworkDocument>();
        var firstCache = firstScope.ServiceProvider.GetRequiredService<IArtworkGraphCache>();
        var secondCache = secondScope.ServiceProvider.GetRequiredService<IArtworkGraphCache>();
        var firstPipeline = firstScope.ServiceProvider.GetRequiredService<IArtworkRenderPipeline>();
        var secondPipeline = secondScope.ServiceProvider.GetRequiredService<IArtworkRenderPipeline>();

        Assert.NotSame(first, second);
        Assert.NotSame(firstCache, secondCache);
        Assert.NotSame(firstPipeline, secondPipeline);
    }

    private sealed class CapturingRegistration : IPluginRegistration, IWorkflowActionRegistration, IWorkbenchCommandRegistration
    {
        public PluginId PluginId => PluginIds.Plugin;
        public IServiceCollection Services { get; } = new ServiceCollection();
        internal List<(DocumentDescriptor Descriptor, Type Model, Type View)> Documents { get; } = [];
        internal List<(DocumentDescriptor Descriptor, Type Model, Type View)> PersistableDocuments { get; } = [];
        internal List<(ToolDescriptor Descriptor, Type Model, Type View)> Tools { get; } = [];
        internal List<(CommandDescriptor Descriptor, DocumentTypeId Target)> Commands { get; } = [];
        internal List<MenuCommandContributionDescriptor> MenuContributions { get; } = [];
        internal List<KeyBindingContributionDescriptor> KeyBindings { get; } = [];

        public void UseLifecycle<TLifecycle>() where TLifecycle : class, IPluginLifecycle => throw new NotSupportedException();

        public void AddDocument<TDocument, TView>(DocumentDescriptor descriptor)
            where TDocument : class, IPluginDocument where TView : Control, new()
        {
            Documents.Add((descriptor, typeof(TDocument), typeof(TView)));
            Services.AddScoped<TDocument>();
            Services.AddTransient<TView>();
        }

        public void AddPersistableDocument<TDocument, TView>(DocumentDescriptor descriptor)
            where TDocument : class, IPersistablePluginDocument where TView : Control, new()
        {
            PersistableDocuments.Add((descriptor, typeof(TDocument), typeof(TView)));
            Services.AddScoped<TDocument>();
            Services.AddTransient<TView>();
        }

        public void AddTool<TTool, TView>(ToolDescriptor descriptor)
            where TTool : class where TView : Control, new() =>
            Tools.Add((descriptor, typeof(TTool), typeof(TView)));

        public void AddWorkflowAction<THandler>(WorkflowActionDescriptor descriptor)
            where THandler : class, IWorkflowActionHandler => throw new NotSupportedException();

        public void UseWorkflowActionGateway() => throw new NotSupportedException();

        public void AddDocumentCommand(CommandDescriptor descriptor, DocumentTypeId targetDocumentTypeId) =>
            Commands.Add((descriptor, targetDocumentTypeId));

        public void AddMenuCommandContribution(MenuCommandContributionDescriptor descriptor) =>
            MenuContributions.Add(descriptor);

        public void AddKeyBindingContribution(KeyBindingContributionDescriptor descriptor) =>
            KeyBindings.Add(descriptor);
    }

    private sealed class NullWindowInteraction : IPluginWindowInteraction
    {
        public Task<IReadOnlyList<string>> PickOpenFilesAsync(
            Avalonia.Platform.Storage.FilePickerOpenOptions options,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> PickSaveFileAsync(
            Avalonia.Platform.Storage.FilePickerSaveOptions options,
            CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<bool> TrySetClipboardTextAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private sealed class TestLifetime : IDocumentLifetime
    {
        public CancellationToken ClosingToken => CancellationToken.None;
        public bool IsClosing => false;
    }
}
