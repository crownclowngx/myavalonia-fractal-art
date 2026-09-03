using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FractalArtPlugin.Features.Artwork;
using FractalArtPlugin.Plugin;
using Microsoft.Extensions.DependencyInjection;
using MyAvaloniaManagement.PluginSdk;
using MyAvaloniaManagement.PluginSdk.UI;

namespace FractalArtPlugin.Standalone;

public sealed partial class App : Avalonia.Application
{
    private ServiceProvider? _provider;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new ServiceCollection();
            services.AddFractalArtPluginServices();
            services.AddSingleton<IPluginWindowInteraction, StandaloneWindowInteraction>();
            services.AddScoped<IDocumentLifetime, PreviewDocumentLifetime>();
            services.AddScoped<FractalArtworkDocument>();
            services.AddTransient<FractalArtworkView>();
            _provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });
            desktop.MainWindow = new MainWindow(_provider);
            desktop.Exit += (_, _) =>
            {
                _provider?.Dispose();
                _provider = null;
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
