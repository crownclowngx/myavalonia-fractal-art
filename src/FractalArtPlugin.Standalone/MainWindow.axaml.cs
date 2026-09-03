using Avalonia.Controls;
using FractalArtPlugin.Features.Artwork;
using Microsoft.Extensions.DependencyInjection;
using MyAvaloniaManagement.PluginSdk;

namespace FractalArtPlugin.Standalone;

public sealed partial class MainWindow : Window
{
    private IServiceScope? _documentScope;

    public MainWindow() => InitializeComponent();

    public MainWindow(IServiceProvider services) : this()
    {
        _documentScope = services.CreateScope();
        var document = _documentScope.ServiceProvider.GetRequiredService<FractalArtworkDocument>();
        var view = _documentScope.ServiceProvider.GetRequiredService<FractalArtworkView>();
        document.InitializeAsync(
            new NewDocumentActivation("分形作品 · Standalone"),
            CancellationToken.None).GetAwaiter().GetResult();
        view.DataContext = document;
        PreviewHost.Content = view;
        Closed += (_, _) =>
        {
            PreviewHost.Content = null;
            _documentScope?.Dispose();
            _documentScope = null;
        };
    }
}
