using Avalonia.Controls;
using System.Diagnostics;
using FractalArtPlugin.Features.Artwork;
using Microsoft.Extensions.DependencyInjection;
using MyAvaloniaManagement.PluginSdk;

namespace FractalArtPlugin.Standalone;

public sealed partial class MainWindow : Window
{
    private IServiceScope? _documentScope;
    private FractalArtworkDocument? _document;
    private Task? _initializationTask;
    private bool _closed;

    public MainWindow() => InitializeComponent();

    public MainWindow(IServiceProvider services) : this()
    {
        _documentScope = services.CreateScope();
        _document = _documentScope.ServiceProvider.GetRequiredService<FractalArtworkDocument>();
        var view = _documentScope.ServiceProvider.GetRequiredService<FractalArtworkView>();
        view.DataContext = _document;
        PreviewHost.Content = view;
        Opened += HandleOpened;
        Closed += HandleClosed;
    }

    /// <summary>
    /// 窗口真正打开后才启动 Document 初始化，使 Avalonia 消息循环能够处理渲染完成后的 UI 续体。
    /// </summary>
    /// <remarks>
    /// 不能在构造函数中对 InitializeAsync 使用 GetResult/Wait：Document 的预览提交需要回到 UI 线程，
    /// 同步等待会占住该线程并形成“进程存在、窗口句柄为 0、没有异常”的循环等待。这里保存返回任务，
    /// 同时由 InitializeDocumentAsync 在任务内部观察全部异常，避免无主的 fire-and-forget 异常。
    /// </remarks>
    private void HandleOpened(object? sender, EventArgs eventArgs)
    {
        Opened -= HandleOpened;
        var document = _document;
        if (document is not null)
        {
            _initializationTask = InitializeDocumentAsync(document);
        }
    }

    private async Task InitializeDocumentAsync(FractalArtworkDocument document)
    {
        try
        {
            await document.InitializeAsync(
                new NewDocumentActivation("分形作品 · Standalone"),
                CancellationToken.None);
        }
        catch (OperationCanceledException) when (_closed)
        {
            // 用户在初始化完成前关闭窗口属于正常生命周期，不展示虚假的启动错误。
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            if (_closed)
            {
                return;
            }

            Title = "Fractal Art · Standalone 初始化失败";
            StartupFailureText.Text = exception.ToString();
            StartupFailurePanel.IsVisible = true;
        }
    }

    private void HandleClosed(object? sender, EventArgs eventArgs)
    {
        _closed = true;
        Opened -= HandleOpened;
        Closed -= HandleClosed;
        PreviewHost.Content = null;
        _document = null;
        _initializationTask = null;
        _documentScope?.Dispose();
        _documentScope = null;
    }
}
