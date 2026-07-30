using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace ArchiveAssist.App;

public partial class CropPagesWindow : Window
{
    private const double MinimumCropSize = 40;
    private readonly double _pdfPageWidth;
    private readonly double _pdfPageHeight;
    private bool _hasAppliedInitialFitZoom;
    private bool _isDraggingCropRectangle;
    private Point _lastMousePosition;

    public CropPagesWindow(BitmapSource pageImage, double pdfPageWidth, double pdfPageHeight)
    {
        InitializeComponent();
        _pdfPageWidth = pdfPageWidth;
        _pdfPageHeight = pdfPageHeight;
        PageImage.Source = pageImage;
        PageImage.Width = pageImage.PixelWidth;
        PageImage.Height = pageImage.PixelHeight;
        CropCanvas.Width = pageImage.PixelWidth;
        CropCanvas.Height = pageImage.PixelHeight;
        Loaded += CropPagesWindowLoaded;
    }

    public double LeftCrop { get; private set; }

    public double TopCrop { get; private set; }

    public double RightCrop { get; private set; }

    public double BottomCrop { get; private set; }

    private double ImageWidth => PageImage.Width;

    private double ImageHeight => PageImage.Height;

    private void CropPagesWindowLoaded(object sender, RoutedEventArgs e)
    {
        ApplyInitialFitZoom();
        var insetX = ImageWidth * 0.08;
        var insetY = ImageHeight * 0.08;
        SetCropRectangle(
            insetX,
            insetY,
            ImageWidth - (insetX * 2),
            ImageHeight - (insetY * 2));
    }

    private void CropZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_hasAppliedInitialFitZoom)
        {
            SetZoom(e.NewValue);
        }
    }

    private void CropScrollViewerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        e.Handled = true;
        var delta = e.Delta > 0 ? 0.1 : -0.1;
        CropZoomSlider.Value = Clamp(
            CropZoomSlider.Value + delta,
            CropZoomSlider.Minimum,
            CropZoomSlider.Maximum);
    }

    private void CropRectangleMouseDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingCropRectangle = true;
        _lastMousePosition = e.GetPosition(CropCanvas);
        CropRectangle.CaptureMouse();
    }

    private void CropRectangleMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingCropRectangle)
        {
            return;
        }

        var currentPosition = e.GetPosition(CropCanvas);
        MoveCropRectangle(
            currentPosition.X - _lastMousePosition.X,
            currentPosition.Y - _lastMousePosition.Y);
        _lastMousePosition = currentPosition;
    }

    private void CropRectangleMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingCropRectangle = false;
        CropRectangle.ReleaseMouseCapture();
    }

    private void TopLeftHandleDragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeFromLeft(ToImageDelta(e.HorizontalChange));
        ResizeFromTop(ToImageDelta(e.VerticalChange));
    }

    private void TopHandleDragDelta(object sender, DragDeltaEventArgs e) =>
        ResizeFromTop(ToImageDelta(e.VerticalChange));

    private void TopRightHandleDragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeFromRight(ToImageDelta(e.HorizontalChange));
        ResizeFromTop(ToImageDelta(e.VerticalChange));
    }

    private void RightHandleDragDelta(object sender, DragDeltaEventArgs e) =>
        ResizeFromRight(ToImageDelta(e.HorizontalChange));

    private void BottomLeftHandleDragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeFromLeft(ToImageDelta(e.HorizontalChange));
        ResizeFromBottom(ToImageDelta(e.VerticalChange));
    }

    private void BottomHandleDragDelta(object sender, DragDeltaEventArgs e) =>
        ResizeFromBottom(ToImageDelta(e.VerticalChange));

    private void BottomRightHandleDragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeFromRight(ToImageDelta(e.HorizontalChange));
        ResizeFromBottom(ToImageDelta(e.VerticalChange));
    }

    private void LeftHandleDragDelta(object sender, DragDeltaEventArgs e) =>
        ResizeFromLeft(ToImageDelta(e.HorizontalChange));

    private void OkClicked(object sender, RoutedEventArgs e)
    {
        var left = Canvas.GetLeft(CropRectangle);
        var top = Canvas.GetTop(CropRectangle);
        var right = left + CropRectangle.Width;
        var bottom = top + CropRectangle.Height;

        LeftCrop = left / ImageWidth * _pdfPageWidth;
        TopCrop = top / ImageHeight * _pdfPageHeight;
        RightCrop = (ImageWidth - right) / ImageWidth * _pdfPageWidth;
        BottomCrop = (ImageHeight - bottom) / ImageHeight * _pdfPageHeight;
        DialogResult = true;
    }

    private void CancelClicked(object sender, RoutedEventArgs e) => DialogResult = false;

    private void MoveCropRectangle(double deltaX, double deltaY)
    {
        var left = Clamp(
            Canvas.GetLeft(CropRectangle) + deltaX,
            0,
            ImageWidth - CropRectangle.Width);
        var top = Clamp(
            Canvas.GetTop(CropRectangle) + deltaY,
            0,
            ImageHeight - CropRectangle.Height);
        SetCropRectangle(left, top, CropRectangle.Width, CropRectangle.Height);
    }

    private void ResizeFromLeft(double horizontalChange)
    {
        var left = Canvas.GetLeft(CropRectangle);
        var right = left + CropRectangle.Width;
        var newLeft = Clamp(left + horizontalChange, 0, right - MinimumCropSize);
        SetCropRectangle(newLeft, Canvas.GetTop(CropRectangle), right - newLeft, CropRectangle.Height);
    }

    private void ResizeFromRight(double horizontalChange)
    {
        var left = Canvas.GetLeft(CropRectangle);
        var newRight = Clamp(
            left + CropRectangle.Width + horizontalChange,
            left + MinimumCropSize,
            ImageWidth);
        SetCropRectangle(left, Canvas.GetTop(CropRectangle), newRight - left, CropRectangle.Height);
    }

    private void ResizeFromTop(double verticalChange)
    {
        var top = Canvas.GetTop(CropRectangle);
        var bottom = top + CropRectangle.Height;
        var newTop = Clamp(top + verticalChange, 0, bottom - MinimumCropSize);
        SetCropRectangle(Canvas.GetLeft(CropRectangle), newTop, CropRectangle.Width, bottom - newTop);
    }

    private void ResizeFromBottom(double verticalChange)
    {
        var top = Canvas.GetTop(CropRectangle);
        var newBottom = Clamp(
            top + CropRectangle.Height + verticalChange,
            top + MinimumCropSize,
            ImageHeight);
        SetCropRectangle(Canvas.GetLeft(CropRectangle), top, CropRectangle.Width, newBottom - top);
    }

    private void SetCropRectangle(double left, double top, double width, double height)
    {
        CropRectangle.Width = width;
        CropRectangle.Height = height;
        Canvas.SetLeft(CropRectangle, left);
        Canvas.SetTop(CropRectangle, top);

        PositionHandle(TopLeftHandle, left, top);
        PositionHandle(TopHandle, left + (width / 2), top);
        PositionHandle(TopRightHandle, left + width, top);
        PositionHandle(RightHandle, left + width, top + (height / 2));
        PositionHandle(BottomLeftHandle, left, top + height);
        PositionHandle(BottomHandle, left + (width / 2), top + height);
        PositionHandle(BottomRightHandle, left + width, top + height);
        PositionHandle(LeftHandle, left, top + (height / 2));
    }

    private static void PositionHandle(Thumb handle, double centerX, double centerY)
    {
        Canvas.SetLeft(handle, centerX - (handle.Width / 2));
        Canvas.SetTop(handle, centerY - (handle.Height / 2));
    }

    private void ApplyInitialFitZoom()
    {
        var availableWidth = Math.Max(1, CropScrollViewer.ActualWidth - 24);
        var availableHeight = Math.Max(1, CropScrollViewer.ActualHeight - 24);
        var fitZoom = Clamp(
            Math.Min(availableWidth / ImageWidth, availableHeight / ImageHeight),
            CropZoomSlider.Minimum,
            CropZoomSlider.Maximum);

        _hasAppliedInitialFitZoom = true;
        CropZoomSlider.Value = fitZoom;
        SetZoom(fitZoom);
    }

    private void SetZoom(double zoom)
    {
        CropCanvasScaleTransform.ScaleX = zoom;
        CropCanvasScaleTransform.ScaleY = zoom;
    }

    private double ToImageDelta(double screenDelta) =>
        screenDelta / Math.Max(0.01, CropZoomSlider.Value);

    private static double Clamp(double value, double minimum, double maximum) =>
        Math.Max(minimum, Math.Min(maximum, value));
}
