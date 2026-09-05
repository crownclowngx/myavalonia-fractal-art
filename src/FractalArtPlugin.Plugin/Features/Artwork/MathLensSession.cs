using CommunityToolkit.Mvvm.ComponentModel;
using FractalArtPlugin.Application;

namespace FractalArtPlugin.Features.Artwork;

internal interface IMathLensPlaybackClock
{
    ValueTask WaitForNextFrameAsync(CancellationToken cancellationToken);
}

/// <summary>生产播放时钟只承担节拍等待；帧推进、边界和生命周期全部由会话控制器决定。</summary>
internal sealed class MathLensPlaybackClock : IMathLensPlaybackClock
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(60);

    public ValueTask WaitForNextFrameAsync(CancellationToken cancellationToken) =>
        new(Task.Delay(Interval, cancellationToken));
}

/// <summary>
/// Document Scope 内的数学透镜会话。它持有分析取消、迟到提交 generation 和播放游标，但从不持有作品的
/// 可编辑副本，也不调用保存、历史或渲染管线。关闭或重开作品时整个对象可直接清空，所以这些状态不会
/// 意外进入 v8 快照、Dirty 修订或导出结果。
/// </summary>
public sealed partial class MathLensSession : ObservableObject, IDisposable
{
    private readonly IMathLensService? _service;
    private readonly IMathLensPlaybackClock _clock;
    private CancellationTokenSource? _analysisCancellation;
    private CancellationTokenSource? _playbackCancellation;
    private long _generation;
    private MathLensSelection _selection = MathLensSelection.Center;
    private bool _disposed;

    internal MathLensSession(IMathLensService? service, IMathLensPlaybackClock? clock = null)
    {
        _service = service;
        _clock = clock ?? new MathLensPlaybackClock();
    }

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _isAnalyzing;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private MathLensAnalysis? _analysis;
    [ObservableProperty] private int _frameIndex;
    [ObservableProperty] private string _status = "打开数学透镜后将解释当前选中的分形层。";

    public MathLensFrame? CurrentFrame => Analysis is { Frames.Count: > 0 }
        ? Analysis.Frames[Math.Clamp(FrameIndex, 0, Analysis.Frames.Count - 1)]
        : null;
    public int FrameMaximum => Math.Max(0, (Analysis?.Frames.Count ?? 1) - 1);
    public string FrameLabel => Analysis is null ? "0 / 0" : $"{FrameIndex + 1} / {Analysis.Frames.Count}";
    public bool HasFrames => Analysis is { Frames.Count: > 0 };
    public bool CanPlay => HasFrames && FrameMaximum > 0 && !IsAnalyzing;
    public bool IsBusy => IsAnalyzing || IsPlaying;

    public async Task OpenAsync(ArtworkDefinition artwork, string selectedLayerId)
    {
        ThrowIfDisposed();
        IsOpen = true;
        _selection = MathLensSelection.Center;
        await AnalyzeAsync(artwork, selectedLayerId, _selection).ConfigureAwait(true);
    }

    public void Close()
    {
        if (_disposed)
        {
            return;
        }

        Interlocked.Increment(ref _generation);
        CancelAndDispose(ref _analysisCancellation);
        CancelAndDispose(ref _playbackCancellation);
        IsAnalyzing = false;
        IsPlaying = false;
        IsOpen = false;
        Analysis = null;
        FrameIndex = 0;
        Status = "数学透镜已关闭，画布交互已恢复。";
    }

    public Task SelectPointAsync(ArtworkDefinition artwork, string selectedLayerId, double x, double y)
    {
        if (!IsOpen || !double.IsFinite(x) || !double.IsFinite(y) || x is < 0 or > 1 || y is < 0 or > 1)
        {
            return Task.CompletedTask;
        }

        _selection = new MathLensSelection(x, y);
        return AnalyzeAsync(artwork, selectedLayerId, _selection);
    }

    /// <summary>
    /// 作品变化后重新读取同一不可变快照。只有当前层仍是同一个逃逸时间家族时才保留点击位置；切层或
    /// 跨家族会回到中心/首帧，避免把旧层坐标误解释为新层坐标。
    /// </summary>
    public Task RefreshAsync(ArtworkDefinition artwork, string selectedLayerId, bool preserveSelection)
    {
        if (!IsOpen)
        {
            return Task.CompletedTask;
        }

        if (!preserveSelection)
        {
            _selection = MathLensSelection.Center;
        }

        return AnalyzeAsync(artwork, selectedLayerId, _selection);
    }

    public void Play()
    {
        ThrowIfDisposed();
        if (!CanPlay)
        {
            return;
        }

        if (FrameIndex >= FrameMaximum)
        {
            FrameIndex = 0;
        }

        CancelAndDispose(ref _playbackCancellation);
        _playbackCancellation = new CancellationTokenSource();
        var token = _playbackCancellation.Token;
        IsPlaying = true;
        _ = RunPlaybackAsync(_playbackCancellation, token);
    }

    public void Pause()
    {
        CancelAndDispose(ref _playbackCancellation);
        IsPlaying = false;
    }

    public void Previous() => MoveTo(FrameIndex - 1);
    public void Next() => MoveTo(FrameIndex + 1);
    public void Reset() => MoveTo(0);

    public void MoveTo(int index)
    {
        if (!HasFrames)
        {
            return;
        }

        Pause();
        FrameIndex = Math.Clamp(index, 0, FrameMaximum);
    }

    /// <summary>取消正在生成的分析和播放，保留已成功提交的分析但回到首帧；半成品永远不会提交。</summary>
    public void Cancel()
    {
        Interlocked.Increment(ref _generation);
        CancelAndDispose(ref _analysisCancellation);
        Pause();
        IsAnalyzing = false;
        if (HasFrames)
        {
            FrameIndex = 0;
        }

        Status = "数学透镜操作已取消。";
    }

    private async Task AnalyzeAsync(
        ArtworkDefinition artwork,
        string selectedLayerId,
        MathLensSelection selection)
    {
        if (_service is null)
        {
            Analysis = MathLensAnalysis.Information(selectedLayerId, "数学透镜不可用", "当前对象图没有登记数学透镜服务。");
            NotifyFrameProperties();
            return;
        }

        CancelAndDispose(ref _analysisCancellation);
        Pause();
        var generation = Interlocked.Increment(ref _generation);
        var current = new CancellationTokenSource();
        var token = current.Token;
        _analysisCancellation = current;
        IsAnalyzing = true;
        Status = "正在读取当前层的数学过程…";
        try
        {
            var result = await _service.AnalyzeAsync(
                artwork, selectedLayerId, selection, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _generation) || !IsOpen)
            {
                return;
            }

            Analysis = result;
            FrameIndex = 0;
            Status = result.Explanation;
            NotifyFrameProperties();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 新分析、关闭和 Dispose 都会经过这里。旧任务不能清空新任务刚提交的状态。
        }
        catch (Exception exception) when (generation == Volatile.Read(ref _generation))
        {
            Analysis = MathLensAnalysis.Information(selectedLayerId, "数学透镜无法分析", exception.Message);
            FrameIndex = 0;
            Status = exception.Message;
            NotifyFrameProperties();
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _analysisCancellation, null, current) == current)
            {
                current.Dispose();
                IsAnalyzing = false;
            }
        }
    }

    private async Task RunPlaybackAsync(CancellationTokenSource current, CancellationToken token)
    {
        try
        {
            while (FrameIndex < FrameMaximum)
            {
                await _clock.WaitForNextFrameAsync(token).ConfigureAwait(true);
                token.ThrowIfCancellationRequested();
                FrameIndex++;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _playbackCancellation, null, current) == current)
            {
                current.Dispose();
                IsPlaying = false;
            }
        }
    }

    partial void OnAnalysisChanged(MathLensAnalysis? value) => NotifyFrameProperties();
    partial void OnFrameIndexChanged(int value) => NotifyFrameProperties();
    partial void OnIsAnalyzingChanged(bool value) => NotifyBusyProperties();
    partial void OnIsPlayingChanged(bool value) => NotifyBusyProperties();

    private void NotifyFrameProperties()
    {
        OnPropertyChanged(nameof(CurrentFrame));
        OnPropertyChanged(nameof(FrameMaximum));
        OnPropertyChanged(nameof(FrameLabel));
        OnPropertyChanged(nameof(HasFrames));
        OnPropertyChanged(nameof(CanPlay));
    }

    private void NotifyBusyProperties()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanPlay));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Close();
        _disposed = true;
    }

    private static void CancelAndDispose(ref CancellationTokenSource? source)
    {
        var current = Interlocked.Exchange(ref source, null);
        current?.Cancel();
        current?.Dispose();
    }
}
