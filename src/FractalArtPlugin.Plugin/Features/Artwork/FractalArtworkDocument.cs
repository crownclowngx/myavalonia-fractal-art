using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FractalArtPlugin.Application;
using MyAvaloniaManagement.PluginSdk;

namespace FractalArtPlugin.Features.Artwork;

/// <summary>分形作品 Document：编排快照、历史、异步渲染、过期结果抑制和导出。</summary>
/// <remarks>
/// 数学、着色、PNG、文件事务和文件窗口都由窄接口承担。Document 只拥有当前不可变配方以及本实例的
/// 生命周期状态；每次渲染先捕获配方，再以 generation 检查结果，旧任务即使迟到也不能覆盖新预览。
/// </remarks>
public sealed partial class FractalArtworkDocument : ObservableObject, IPersistablePluginDocument, IDisposable
{
    private readonly IArtworkValidator _validator;
    private readonly IArtworkSnapshotCodec _snapshotCodec;
    private readonly IArtworkRenderPipeline _renderPipeline;
    private readonly IPreviewImageFactory _previewImageFactory;
    private readonly IArtworkExporter _exporter;
    private readonly IArtworkExportDialog _exportDialog;
    private readonly IArtworkHistory _history;
    private readonly IDocumentLifetime _lifetime;
    private ArtworkDefinition _artwork = ArtworkDefinition.CreateDefault();
    private DocumentPresentationState _presentation = new("分形作品");
    private CancellationTokenSource? _previewCancellation;
    private CancellationTokenSource? _exportCancellation;
    private long _previewGeneration;
    private long _revision;
    private long _acceptedRevision;
    private ArtworkDefinition? _viewportInteractionStart;
    private bool _disposed;

    public FractalArtworkDocument(
        IArtworkValidator validator,
        IArtworkSnapshotCodec snapshotCodec,
        IArtworkRenderPipeline renderPipeline,
        IPreviewImageFactory previewImageFactory,
        IArtworkExporter exporter,
        IArtworkExportDialog exportDialog,
        IArtworkHistory history,
        IDocumentLifetime lifetime)
    {
        _validator = validator;
        _snapshotCodec = snapshotCodec;
        _renderPipeline = renderPipeline;
        _previewImageFactory = previewImageFactory;
        _exporter = exporter;
        _exportDialog = exportDialog;
        _history = history;
        _lifetime = lifetime;
    }

    [ObservableProperty] private Bitmap? _previewImage;
    [ObservableProperty] private bool _isRendering;
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private string _statusMessage = "正在准备 Julia 预览…";
    [ObservableProperty] private string _lastPreviewFingerprint = string.Empty;
    [ObservableProperty] private TransientPreviewTransform _transientPreview = TransientPreviewTransform.Identity;

    public DocumentPresentationState Presentation => _presentation;
    public bool IsDirty => _revision != _acceptedRevision;
    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;
    public bool IsOperationBusy => IsRendering || IsExporting;
    public bool IsPreviewEmpty => PreviewImage is null;

    internal ArtworkDefinition Artwork => _artwork;

    public int CanvasWidth
    {
        get => _artwork.Canvas.Width;
        set => Mutate(value == CanvasWidth ? _artwork : _artwork with { Canvas = _artwork.Canvas with { Width = value } });
    }

    public int CanvasHeight
    {
        get => _artwork.Canvas.Height;
        set => Mutate(value == CanvasHeight ? _artwork : _artwork with { Canvas = _artwork.Canvas with { Height = value } });
    }

    public string BackgroundHex
    {
        get => _artwork.Canvas.Background.ToHex();
        set => SetColor(value, _artwork.Canvas.Background, color => _artwork with { Canvas = _artwork.Canvas with { Background = color } });
    }

    public long Seed
    {
        get => _artwork.Seed;
        set => Mutate(value == Seed ? _artwork : _artwork with { Seed = value });
    }

    public string CenterX
    {
        get => _artwork.Julia.CenterX;
        set => SetHighPrecisionNumber(value, CenterX, number =>
            _artwork with { Julia = _artwork.Julia with { CenterX = number } });
    }

    public string CenterY
    {
        get => _artwork.Julia.CenterY;
        set => SetHighPrecisionNumber(value, CenterY, number =>
            _artwork with { Julia = _artwork.Julia with { CenterY = number } });
    }

    public string Scale
    {
        get => _artwork.Julia.Scale;
        set => SetHighPrecisionNumber(value, Scale, number =>
            _artwork with { Julia = _artwork.Julia with { Scale = number } });
    }

    public string ConstantReal
    {
        get => _artwork.Julia.ConstantReal;
        set => SetHighPrecisionNumber(value, ConstantReal, number =>
            _artwork with { Julia = _artwork.Julia with { ConstantReal = number } });
    }

    public string ConstantImaginary
    {
        get => _artwork.Julia.ConstantImaginary;
        set => SetHighPrecisionNumber(value, ConstantImaginary, number =>
            _artwork with { Julia = _artwork.Julia with { ConstantImaginary = number } });
    }

    public int MaxIterations
    {
        get => _artwork.Julia.MaxIterations;
        set => Mutate(value == MaxIterations ? _artwork : _artwork with { Julia = _artwork.Julia with { MaxIterations = value } });
    }

    public bool ForceHighPrecision
    {
        get => _artwork.Julia.ForceHighPrecision;
        set => Mutate(value == ForceHighPrecision
            ? _artwork
            : _artwork with { Julia = _artwork.Julia with { ForceHighPrecision = value } });
    }

    public int PrecisionDigits
    {
        get => _artwork.Julia.PrecisionDigits;
        set
        {
            if (value == PrecisionDigits)
            {
                return;
            }

            TryMutate(_artwork with { Julia = _artwork.Julia with { PrecisionDigits = value } });
        }
    }

    public string GradientStartHex
    {
        get => _artwork.Gradient.Start.ToHex();
        set => SetColor(value, _artwork.Gradient.Start, color => _artwork with { Gradient = _artwork.Gradient with { Start = color } });
    }

    public string GradientEndHex
    {
        get => _artwork.Gradient.End.ToHex();
        set => SetColor(value, _artwork.Gradient.End, color => _artwork with { Gradient = _artwork.Gradient with { End = color } });
    }

    public bool HighQualityPreview
    {
        get => _artwork.Presentation.HighQualityPreview;
        set => Mutate(value == HighQualityPreview
            ? _artwork
            : _artwork with { Presentation = _artwork.Presentation with { HighQualityPreview = value } });
    }

    public event EventHandler? PresentationChanged;
    public event EventHandler? IsDirtyChanged;

    /// <summary>
    /// 新建时使用默认完整配方，恢复时先严格解码到局部变量。只有解码和验证全部成功才替换当前状态，
    /// 因此取消、损坏内容或未知版本不会留下半初始化 Document。
    /// </summary>
    public async ValueTask InitializeAsync(DocumentActivation activation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activation);
        cancellationToken.ThrowIfCancellationRequested();

        var candidate = activation is RestoreDocumentActivation restore
            ? _snapshotCodec.Decode(restore.RestoredContent)
            : ArtworkDefinition.CreateDefault();
        _validator.Validate(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        _artwork = candidate;
        _history.Clear();
        _revision = _acceptedRevision = 0;
        _presentation = new DocumentPresentationState(
            string.IsNullOrWhiteSpace(activation.Title) ? "分形作品" : activation.Title);
        PresentationChanged?.Invoke(this, EventArgs.Empty);
        NotifyArtworkProperties();
        await RenderPreviewCoreAsync(debounce: false, cancellationToken).ConfigureAwait(true);
    }

    public ValueTask<DocumentSaveSnapshot> CaptureSaveSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var content = _snapshotCodec.Encode(_artwork);
        return ValueTask.FromResult(new DocumentSaveSnapshot(new DocumentRevision(_revision), content));
    }

    public void AcceptChanges(DocumentRevision savedRevision)
    {
        var wasDirty = IsDirty;
        if (savedRevision.Value == _revision)
        {
            _acceptedRevision = _revision;
        }

        if (wasDirty != IsDirty)
        {
            IsDirtyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private Task RefreshPreviewAsync() => RenderPreviewCoreAsync(debounce: false, CancellationToken.None);

    [RelayCommand]
    private void Undo()
    {
        if (!_history.CanUndo)
        {
            return;
        }

        ApplyHistory(_history.Undo(_artwork));
    }

    [RelayCommand]
    private void Redo()
    {
        if (!_history.CanRedo)
        {
            return;
        }

        ApplyHistory(_history.Redo(_artwork));
    }

    [RelayCommand]
    private async Task ExportPngAsync()
    {
        CancelAndDispose(ref _exportCancellation);
        _exportCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ClosingToken);
        var current = _exportCancellation;
        var token = current.Token;
        try
        {
            var path = await _exportDialog.PickPngPathAsync("fractal-art.png", token).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(path))
            {
                StatusMessage = "已取消 PNG 导出。";
                return;
            }

            IsExporting = true;
            StatusMessage = $"正在以 {CanvasWidth}×{CanvasHeight} 最终质量重新渲染…";
            var snapshot = _artwork;
            await _exporter.ExportAsync(snapshot, path, token).ConfigureAwait(true);
            if (!_lifetime.IsClosing)
            {
                StatusMessage = $"PNG 已原子导出：{path}";
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (!_lifetime.IsClosing)
            {
                StatusMessage = "导出已取消，未报告成功。";
            }
        }
        catch (Exception exception)
        {
            StatusMessage = $"导出失败：{exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(current, _exportCancellation))
            {
                _exportCancellation = null;
                current.Dispose();
                IsExporting = false;
            }
        }
    }

    [RelayCommand]
    private void CancelOperation()
    {
        _previewCancellation?.Cancel();
        _exportCancellation?.Cancel();
    }

    internal Task RenderPreviewNowAsync(CancellationToken cancellationToken = default) =>
        RenderPreviewCoreAsync(debounce: false, cancellationToken);

    /// <summary>开始一次连续拖动画布；拖动期间可以多次刷新，但撤销历史只记录手势开始前的一份作品。</summary>
    internal void BeginViewportInteraction()
    {
        ThrowIfDisposed();
        _viewportInteractionStart ??= _artwork;
    }

    internal void PanViewport(double deltaX, double deltaY, double viewportHeight)
    {
        var candidate = _artwork with
        {
            Julia = HighPrecisionViewport.Pan(_artwork.Julia, deltaX, deltaY, viewportHeight)
        };
        TransientPreview = TransientPreview.Pan(deltaX, deltaY);
        TryMutate(candidate, recordHistory: _viewportInteractionStart is null);
    }

    internal void EndViewportInteraction()
    {
        var start = _viewportInteractionStart;
        _viewportInteractionStart = null;
        if (start is null || start == _artwork)
        {
            return;
        }

        _history.Record(start);
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    /// <summary>围绕鼠标指向的复平面位置缩放；中心和尺度全程使用作品声明的十进制精度。</summary>
    internal void ZoomViewport(
        double pointerX,
        double pointerY,
        double viewportWidth,
        double viewportHeight,
        double wheelDelta)
    {
        var candidate = _artwork with
        {
            Julia = HighPrecisionViewport.ZoomAt(
                _artwork.Julia,
                pointerX,
                pointerY,
                viewportWidth,
                viewportHeight,
                wheelDelta)
        };
        if (candidate == _artwork)
        {
            return;
        }

        var steps = Math.Clamp((int)Math.Ceiling(Math.Abs(wheelDelta)), 1, 8);
        var factor = Math.Pow(wheelDelta > 0 ? 0.8d : 1.25d, steps);
        TransientPreview = TransientPreview.Zoom(factor, pointerX, pointerY);
        TryMutate(candidate);
    }

    private void Mutate(ArtworkDefinition candidate, bool recordHistory = true)
    {
        ThrowIfDisposed();
        if (ReferenceEquals(candidate, _artwork) || candidate == _artwork)
        {
            return;
        }

        _validator.Validate(candidate);
        var wasDirty = IsDirty;
        if (recordHistory)
        {
            _history.Record(_artwork);
        }
        _artwork = candidate;
        _revision++;
        NotifyArtworkProperties();
        NotifyHistoryAndDirty(wasDirty);
        _ = RenderPreviewCoreAsync(debounce: true, CancellationToken.None);
    }

    private void TryMutate(ArtworkDefinition candidate, bool recordHistory = true)
    {
        try
        {
            Mutate(candidate, recordHistory);
        }
        catch (InvalidDataException exception)
        {
            StatusMessage = exception.Message;
        }
    }

    private void SetHighPrecisionNumber(
        string? text,
        string current,
        Func<string, ArtworkDefinition> createCandidate)
    {
        if (!ArbitraryDecimal.TryParse(text, out var value))
        {
            StatusMessage = "请输入普通十进制或科学计数法，例如 -0.745、1e-40。";
            return;
        }

        var normalized = value.Round(PrecisionDigits).ToString();
        if (!string.Equals(normalized, current, StringComparison.Ordinal))
        {
            TryMutate(createCandidate(normalized));
        }
    }

    private void SetColor(
        string? text,
        RgbaColor current,
        Func<RgbaColor, ArtworkDefinition> createCandidate)
    {
        if (!RgbaColor.TryParse(text, out var color))
        {
            StatusMessage = "颜色必须是 #RRGGBB 或 #RRGGBBAA。";
            return;
        }

        if (color != current)
        {
            Mutate(createCandidate(color));
        }
    }

    private void ApplyHistory(ArtworkDefinition candidate)
    {
        var wasDirty = IsDirty;
        _artwork = candidate;
        _revision++;
        NotifyArtworkProperties();
        NotifyHistoryAndDirty(wasDirty);
        _ = RenderPreviewCoreAsync(debounce: false, CancellationToken.None);
    }

    private async Task RenderPreviewCoreAsync(bool debounce, CancellationToken externalCancellation)
    {
        ThrowIfDisposed();
        var current = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.ClosingToken,
            externalCancellation);
        var previous = Interlocked.Exchange(ref _previewCancellation, current);
        previous?.Cancel();
        previous?.Dispose();
        var generation = Interlocked.Increment(ref _previewGeneration);
        IsRendering = true;

        try
        {
            var snapshot = _artwork;
            var requestedContext = RenderContext.ForPreview(snapshot);
            // 连续输入先提交最低成本的真实预览；若用户开启精细预览，则同一 generation 在稳定后再提升质量。
            // 两次结果都来自真实数值管线，暂态 Bitmap 变换只负责填补首个结果到达前的感知延迟。
            var context = debounce && snapshot.Presentation.HighQualityPreview
                ? RenderContext.ForPreview(snapshot with
                {
                    Presentation = snapshot.Presentation with { HighQualityPreview = false }
                })
                : requestedContext;
            var precisionLabel = context.NumericPrecision == NumericPrecision.Arbitrary
                ? $"任意精度 {context.EffectivePrecisionDigits}/{context.ConfiguredPrecisionDigits} 位"
                : "double 快速模式";
            StatusMessage = $"正在渲染 {context.Width}×{context.Height} 交互预览 · {precisionLabel}…";
            if (debounce)
            {
                await Task.Delay(120, current.Token).ConfigureAwait(true);
            }

            var result = await _renderPipeline.RenderAsync(snapshot, context, current.Token).ConfigureAwait(true);
            if (!TryCommitPreview(result, context, generation, current.Token))
            {
                return;
            }

            if (debounce && requestedContext.Width != context.Width)
            {
                await Task.Delay(160, current.Token).ConfigureAwait(true);
                var detailed = await _renderPipeline.RenderAsync(snapshot, requestedContext, current.Token).ConfigureAwait(true);
                TryCommitPreview(detailed, requestedContext, generation, current.Token);
            }
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested)
        {
            // 新请求、Document 关闭或显式取消都属于正常控制流；只有当前代次才更新用户状态。
            if (generation == Volatile.Read(ref _previewGeneration) && !_lifetime.IsClosing)
            {
                StatusMessage = "预览已取消。";
            }
        }
        catch (Exception exception)
        {
            if (generation == Volatile.Read(ref _previewGeneration))
            {
                StatusMessage = $"预览失败：{exception.Message}";
            }
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _previewCancellation, null, current) == current)
            {
                current.Dispose();
                IsRendering = false;
            }
        }
    }

    /// <summary>
    /// 真实帧提交的唯一入口。generation 检查位于 Bitmap 创建前后；即使底层忽略取消，旧帧也无法覆盖新状态。
    /// 成功提交后立即清除暂态变换，确保屏幕重新与真实像素坐标对齐。
    /// </summary>
    private bool TryCommitPreview(RgbaImage result, RenderContext context, long generation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (generation != Volatile.Read(ref _previewGeneration) || _lifetime.IsClosing || _disposed)
        {
            return false;
        }

        var bitmap = _previewImageFactory.Create(result, token);
        if (generation != Volatile.Read(ref _previewGeneration) || _lifetime.IsClosing || _disposed)
        {
            bitmap?.Dispose();
            return false;
        }

        var old = PreviewImage;
        PreviewImage = bitmap;
        old?.Dispose();
        TransientPreview = TransientPreviewTransform.Identity;
        LastPreviewFingerprint = RenderFingerprint.Create(result);
        var diagnostics = result.Diagnostics;
        var fallback = diagnostics is { PrecisionFallbackPixels: > 0 }
            ? $" · 回退 {diagnostics.PrecisionFallbackPixels} 像素"
            : string.Empty;
        var precision = context.NumericPrecision == NumericPrecision.Arbitrary
            ? $"任意精度 {context.EffectivePrecisionDigits}/{context.ConfiguredPrecisionDigits} 位"
            : "double 快速模式";
        StatusMessage = $"预览完成 · {precision}{fallback} · renderer v{context.RendererVersion} · {LastPreviewFingerprint}";
        return true;
    }

    private void NotifyArtworkProperties()
    {
        OnPropertyChanged(nameof(CanvasWidth));
        OnPropertyChanged(nameof(CanvasHeight));
        OnPropertyChanged(nameof(BackgroundHex));
        OnPropertyChanged(nameof(Seed));
        OnPropertyChanged(nameof(CenterX));
        OnPropertyChanged(nameof(CenterY));
        OnPropertyChanged(nameof(Scale));
        OnPropertyChanged(nameof(ConstantReal));
        OnPropertyChanged(nameof(ConstantImaginary));
        OnPropertyChanged(nameof(MaxIterations));
        OnPropertyChanged(nameof(ForceHighPrecision));
        OnPropertyChanged(nameof(PrecisionDigits));
        OnPropertyChanged(nameof(GradientStartHex));
        OnPropertyChanged(nameof(GradientEndHex));
        OnPropertyChanged(nameof(HighQualityPreview));
    }

    private void NotifyHistoryAndDirty(bool wasDirty)
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        if (wasDirty != IsDirty)
        {
            IsDirtyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    partial void OnIsRenderingChanged(bool value) => OnPropertyChanged(nameof(IsOperationBusy));
    partial void OnIsExportingChanged(bool value) => OnPropertyChanged(nameof(IsOperationBusy));
    partial void OnPreviewImageChanged(Bitmap? value) => OnPropertyChanged(nameof(IsPreviewEmpty));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Increment(ref _previewGeneration);
        CancelAndDispose(ref _previewCancellation);
        CancelAndDispose(ref _exportCancellation);
        PreviewImage?.Dispose();
        PreviewImage = null;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void CancelAndDispose(ref CancellationTokenSource? source)
    {
        var current = Interlocked.Exchange(ref source, null);
        current?.Cancel();
        current?.Dispose();
    }
}
