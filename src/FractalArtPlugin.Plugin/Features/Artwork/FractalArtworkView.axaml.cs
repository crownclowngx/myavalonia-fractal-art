using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System.ComponentModel;

namespace FractalArtPlugin.Features.Artwork;

public sealed partial class FractalArtworkView : UserControl
{
    private bool _isDragging;
    private Avalonia.Point _lastPointerPosition;
    private INotifyPropertyChanged? _observedDocument;

    public FractalArtworkView()
    {
        InitializeComponent();
        DataContextChanged += HandleDataContextChanged;
    }

    private void HandleDataContextChanged(object? sender, EventArgs eventArgs)
    {
        if (_observedDocument is not null)
        {
            _observedDocument.PropertyChanged -= HandleDocumentPropertyChanged;
        }

        _observedDocument = DataContext as INotifyPropertyChanged;
        if (_observedDocument is not null)
        {
            _observedDocument.PropertyChanged += HandleDocumentPropertyChanged;
        }

        ApplyTransientTransform();
    }

    private void HandleDocumentPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(FractalArtworkDocument.TransientPreview))
        {
            ApplyTransientTransform();
        }
    }

    /// <summary>
    /// 只变换上一张 Bitmap 的呈现，不生成新像素，也不参与保存、导出或指纹。
    /// 当 Document 提交真实帧时状态恢复为 Identity，本方法同步移除全部插值变换。
    /// </summary>
    private void ApplyTransientTransform()
    {
        var state = DataContext is FractalArtworkDocument document
            ? document.TransientPreview
            : TransientPreviewTransform.Identity;
        if (state.IsIdentity)
        {
            PreviewImageControl.RenderTransform = null;
            return;
        }

        PreviewImageControl.RenderTransform = new TransformGroup
        {
            Children =
            {
                new TranslateTransform(-state.OriginX, -state.OriginY),
                new ScaleTransform(state.Scale, state.Scale),
                new TranslateTransform(
                    state.OriginX + state.OffsetX,
                    state.OriginY + state.OffsetY)
            }
        };
    }

    /// <summary>把 Avalonia 指针输入适配为 Document 的高精度视口意图；View 不直接计算复平面坐标。</summary>
    private void HandlePointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        var point = eventArgs.GetCurrentPoint(CanvasInteractionSurface);
        if (!point.Properties.IsLeftButtonPressed || DataContext is not FractalArtworkDocument document)
        {
            return;
        }

        _isDragging = true;
        _lastPointerPosition = point.Position;
        document.BeginViewportInteraction();
        eventArgs.Pointer.Capture(CanvasInteractionSurface);
        eventArgs.Handled = true;
    }

    private void HandlePointerMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (!_isDragging || DataContext is not FractalArtworkDocument document)
        {
            return;
        }

        var position = eventArgs.GetPosition(CanvasInteractionSurface);
        var delta = position - _lastPointerPosition;
        _lastPointerPosition = position;
        document.PanViewport(delta.X, delta.Y, CanvasInteractionSurface.Bounds.Height);
        eventArgs.Handled = true;
    }

    private void HandlePointerReleased(object? sender, PointerReleasedEventArgs eventArgs)
    {
        EndDrag(eventArgs.Pointer);
        eventArgs.Handled = true;
    }

    private void HandlePointerCaptureLost(object? sender, PointerCaptureLostEventArgs eventArgs) => EndDrag(null);

    private void HandlePointerWheelChanged(object? sender, PointerWheelEventArgs eventArgs)
    {
        if (DataContext is not FractalArtworkDocument document)
        {
            return;
        }

        var position = eventArgs.GetPosition(CanvasInteractionSurface);
        var bounds = CanvasInteractionSurface.Bounds;
        document.ZoomViewport(position.X, position.Y, bounds.Width, bounds.Height, eventArgs.Delta.Y);
        eventArgs.Handled = true;
    }

    private void EndDrag(IPointer? pointer)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        pointer?.Capture(null);
        if (DataContext is FractalArtworkDocument document)
        {
            document.EndViewportInteraction();
        }
    }
}
