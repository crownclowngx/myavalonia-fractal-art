using Avalonia.Controls;
using Avalonia.Input;

namespace FractalArtPlugin.Features.Artwork;

public sealed partial class FractalArtworkView : UserControl
{
    private bool _isDragging;
    private Avalonia.Point _lastPointerPosition;

    public FractalArtworkView() => InitializeComponent();

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
