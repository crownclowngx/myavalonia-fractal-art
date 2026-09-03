using MyAvaloniaManagement.PluginSdk;

namespace FractalArtPlugin.Standalone;

/// <summary>Standalone 中每个 Document Scope 独占关闭信号，模拟 Host 的永久关闭语义。</summary>
internal sealed class PreviewDocumentLifetime : IDocumentLifetime, IDisposable
{
    private readonly CancellationTokenSource _closing = new();

    public CancellationToken ClosingToken => _closing.Token;
    public bool IsClosing => _closing.IsCancellationRequested;

    public void Dispose()
    {
        if (!_closing.IsCancellationRequested)
        {
            _closing.Cancel();
        }

        _closing.Dispose();
    }
}
