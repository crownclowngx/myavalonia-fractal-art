using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using MyAvaloniaManagement.PluginSdk.UI;

namespace FractalArtPlugin.Standalone;

/// <summary>Standalone 通过自己的窗口完成真实文件选择，但不把 Window 泄漏给插件业务层。</summary>
internal sealed class StandaloneWindowInteraction : IPluginWindowInteraction
{
    public async Task<IReadOnlyList<string>> PickOpenFilesAsync(
        FilePickerOpenOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        var provider = GetMainWindow()?.StorageProvider;
        if (provider is null)
        {
            return [];
        }

        var files = await provider.OpenFilePickerAsync(options);
        cancellationToken.ThrowIfCancellationRequested();
        return files.Select(file => file.TryGetLocalPath()).Where(path => path is not null).Cast<string>().ToArray();
    }

    public async Task<string?> PickSaveFileAsync(
        FilePickerSaveOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        var provider = GetMainWindow()?.StorageProvider;
        if (provider is null)
        {
            return null;
        }

        var file = await provider.SaveFilePickerAsync(options);
        cancellationToken.ThrowIfCancellationRequested();
        return file?.TryGetLocalPath();
    }

    public Task<bool> TrySetClipboardTextAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }

    private static Window? GetMainWindow() =>
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}
