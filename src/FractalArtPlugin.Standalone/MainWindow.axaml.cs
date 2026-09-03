using Avalonia.Controls;
using FractalArtPlugin.Features.Main;
using MyAvaloniaManagement.PluginSdk;

namespace FractalArtPlugin.Standalone;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var document = new MainDocument();
        document.InitializeAsync(
            new NewDocumentActivation("FractalArtPlugin Standalone"),
            CancellationToken.None).GetAwaiter().GetResult();
        DataContext = document;
    }
}
