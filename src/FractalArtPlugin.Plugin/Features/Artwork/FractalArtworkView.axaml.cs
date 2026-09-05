using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System.ComponentModel;
using Avalonia.Interactivity;
using Avalonia.Controls.Shapes;
using FractalArtPlugin.Application;

namespace FractalArtPlugin.Features.Artwork;

public sealed partial class FractalArtworkView : UserControl
{
    private bool _isDragging;
    private Avalonia.Point _lastPointerPosition;
    private INotifyPropertyChanged? _observedDocument;
    private MathLensSession? _observedMathLens;

    public FractalArtworkView()
    {
        InitializeComponent();
        DataContextChanged += HandleDataContextChanged;
        MathLensOverlayCanvas.SizeChanged += (_, _) => RenderMathLensOverlay();
    }

    private void HandleDataContextChanged(object? sender, EventArgs eventArgs)
    {
        if (_observedDocument is not null)
        {
            _observedDocument.PropertyChanged -= HandleDocumentPropertyChanged;
        }

        if (_observedMathLens is not null)
        {
            _observedMathLens.PropertyChanged -= HandleMathLensPropertyChanged;
        }

        _observedDocument = DataContext as INotifyPropertyChanged;
        if (_observedDocument is not null)
        {
            _observedDocument.PropertyChanged += HandleDocumentPropertyChanged;
        }


        _observedMathLens = (DataContext as FractalArtworkDocument)?.MathLens;
        if (_observedMathLens is not null)
        {
            _observedMathLens.PropertyChanged += HandleMathLensPropertyChanged;
        }

        ApplyTransientTransform();
        RenderMathLensOverlay();
    }

    private void HandleDocumentPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(FractalArtworkDocument.TransientPreview))
        {
            ApplyTransientTransform();
        }
        else if (eventArgs.PropertyName is nameof(FractalArtworkDocument.PreviewImage) or
                 nameof(FractalArtworkDocument.IsMathLensOpen))
        {
            RenderMathLensOverlay();
        }
    }

    private void HandleMathLensPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(MathLensSession.CurrentFrame) or
            nameof(MathLensSession.Analysis) or nameof(MathLensSession.IsOpen))
        {
            RenderMathLensOverlay();
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
    private async void HandlePointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        var point = eventArgs.GetCurrentPoint(CanvasInteractionSurface);
        if (!point.Properties.IsLeftButtonPressed || DataContext is not FractalArtworkDocument document)
        {
            return;
        }

        if (document.MathLens.IsOpen)
        {
            var position = eventArgs.GetPosition(MathLensOverlayCanvas);
            if (TryMapToPreview(position, out var normalized))
            {
                await document.SelectMathLensPointAsync(normalized.X, normalized.Y);
            }

            eventArgs.Handled = true;
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

        if (document.MathLens.IsOpen)
        {
            eventArgs.Handled = true;
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

    /// <summary>
    /// 候选卡片位于 DataTemplate 内，按钮的 DataContext 是候选而非 Document。代码隐藏只负责把模板事件
    /// 转成 Document 命令调用，不读取或修改任何领域参数。
    /// </summary>
    private void HandleApplyVariation(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is FractalArtworkDocument document &&
            sender is Button { DataContext: VariationCandidateItem item })
        {
            document.ApplyVariationCommand.Execute(item);
        }
    }

    private void HandleToggleFavorite(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is FractalArtworkDocument document &&
            sender is Button { DataContext: VariationCandidateItem item })
        {
            document.ToggleFavoriteCommand.Execute(item);
        }
    }

    private async void HandleContinueVariation(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is FractalArtworkDocument document &&
            sender is Button { DataContext: VariationCandidateItem item })
        {
            await document.ContinueFromVariationCommand.ExecuteAsync(item);
        }
    }

    private void HandleRestoreFavorite(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is FractalArtworkDocument document &&
            sender is Button { DataContext: FavoriteVariationDefinition favorite })
        {
            document.RestoreFavoriteCommand.Execute(favorite);
        }
    }

    /// <summary>
    /// 图层行位于 DataTemplate 内，按钮拿到的是行投影。View 只把稳定 ID 交给 Document，
    /// 选择规则、历史和异步渲染仍集中在 Document，避免 UI 层直接改领域树。
    /// </summary>
    private void HandleSelectLayer(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is FractalArtworkDocument document &&
            sender is Button { DataContext: ArtworkLayerItem item })
        {
            document.SelectLayerCommand.Execute(item);
        }
    }

    /// <summary>显隐操作同样按图层 ID 路由，确保模板复用和排序后不会误改其他图层。</summary>
    private void HandleToggleLayerVisibility(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is FractalArtworkDocument document &&
            sender is Button { DataContext: ArtworkLayerItem item })
        {
            document.ToggleLayerVisibilityCommand.Execute(item);
        }
    }

    private async void HandleContinueFavorite(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is FractalArtworkDocument document &&
            sender is Button { DataContext: FavoriteVariationDefinition favorite })
        {
            await document.ContinueFromFavoriteCommand.ExecuteAsync(favorite);
        }
    }


    /// <summary>
    /// Image 的 Uniform 会在宽高比不同时留下信箱边。点击必须先落入真实图片矩形，再换算成 0–1 坐标；
    /// 不能直接除以外层 Border 尺寸，否则数学透镜在宽屏窗口中会选到错误的复平面像素。
    /// </summary>
    private bool TryMapToPreview(Avalonia.Point point, out MathLensSelection normalized)
    {
        normalized = default;
        if (!TryGetPreviewProjection(out var projection) || projection.TryNormalize(point.X, point.Y) is not { } mapped)
        {
            return false;
        }

        normalized = mapped;
        return true;
    }

    private bool TryGetPreviewRect(out Rect rect)
    {
        rect = default;
        if (!TryGetPreviewProjection(out var projection))
        {
            return false;
        }

        rect = new Rect(projection.X, projection.Y, projection.Width, projection.Height);
        return true;
    }

    private bool TryGetPreviewProjection(out UniformImageProjection projection)
    {
        projection = default;
        if (PreviewImageControl.Source is not Avalonia.Media.Imaging.Bitmap bitmap ||
            UniformImageProjection.Create(
                MathLensOverlayCanvas.Bounds.Width,
                MathLensOverlayCanvas.Bounds.Height,
                bitmap.PixelSize.Width,
                bitmap.PixelSize.Height) is not { } resolved)
        {
            return false;
        }

        projection = resolved;
        return true;
    }

    /// <summary>
    /// Overlay 使用两个 StreamGeometry 批量绘制线与点，避免为最多两万个吸引子展示点创建两万个 Avalonia
    /// 控件。所有输入都是不可变归一化坐标；这里仅做最终像素投影，不参与公式或播放状态计算。
    /// </summary>
    private void RenderMathLensOverlay()
    {
        MathLensOverlayCanvas.Children.Clear();
        if (_observedMathLens is not { IsOpen: true, CurrentFrame: { } frame } ||
            !TryGetPreviewRect(out var rect))
        {
            return;
        }

        var lineGeometry = new StreamGeometry();
        using (var context = lineGeometry.Open())
        {
            for (var index = 0; index < Math.Min(frame.VisibleSegmentCount, frame.Segments.Count); index++)
            {
                var segment = frame.Segments[index];
                if (!TryProject(segment.Start, rect, out var start) || !TryProject(segment.End, rect, out var end))
                {
                    continue;
                }

                context.BeginFigure(start, false);
                context.LineTo(end);
                context.EndFigure(false);
            }
        }

        MathLensOverlayCanvas.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Data = lineGeometry,
            Stroke = new SolidColorBrush(Color.FromArgb(225, 255, 209, 102)),
            StrokeThickness = 1.6
        });

        var pointGeometry = new StreamGeometry();
        using (var context = pointGeometry.Open())
        {
            for (var index = 0; index < Math.Min(frame.VisiblePointCount, frame.Points.Count); index++)
            {
                if (!TryProject(frame.Points[index], rect, out var point))
                {
                    continue;
                }

                context.BeginFigure(point, false);
                context.LineTo(new Avalonia.Point(point.X + 0.8, point.Y));
                context.EndFigure(false);
            }
        }

        MathLensOverlayCanvas.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Data = pointGeometry,
            Stroke = new SolidColorBrush(Color.FromArgb(190, 120, 225, 255)),
            StrokeThickness = 1.2
        });

        if (frame.Marker is { } marker && TryProject(marker, rect, out var markerPoint))
        {
            var ellipse = new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = new SolidColorBrush(Color.FromArgb(90, 255, 209, 102)),
                Stroke = Brushes.White,
                StrokeThickness = 1.5
            };
            Canvas.SetLeft(ellipse, markerPoint.X - 5);
            Canvas.SetTop(ellipse, markerPoint.Y - 5);
            MathLensOverlayCanvas.Children.Add(ellipse);
        }
    }

    private static bool TryProject(MathLensPoint source, Rect rect, out Avalonia.Point point)
    {
        point = default;
        if (!double.IsFinite(source.X) || !double.IsFinite(source.Y))
        {
            return false;
        }

        point = new Avalonia.Point(rect.X + source.X * rect.Width, rect.Y + source.Y * rect.Height);
        return true;
    }
}
