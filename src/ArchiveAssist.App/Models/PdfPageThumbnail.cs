using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ArchiveAssist.Core.Models;

namespace ArchiveAssist.App.Models;

public sealed class PdfPageThumbnail : INotifyPropertyChanged
{
    private int _pageIndex;
    private int _pageNumber;
    private int _rotationDegrees;
    private bool _isCropped;
    private ImageSource? _thumbnailImage;
    private string? _renderError;
    private double _thumbnailLongEdge = 150;

    public PdfPageThumbnail(PdfEditPage page)
    {
        Id = page.Id;
        UpdateFrom(page);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; }

    public int PageIndex
    {
        get => _pageIndex;
        private set => SetProperty(ref _pageIndex, value);
    }

    public int PageNumber
    {
        get => _pageNumber;
        private set
        {
            if (!SetProperty(ref _pageNumber, value))
            {
                return;
            }

            OnPropertyChanged(nameof(PreviewLabel));
        }
    }

    public int RotationDegrees
    {
        get => _rotationDegrees;
        private set
        {
            if (!SetProperty(ref _rotationDegrees, value))
            {
                return;
            }

            OnPropertyChanged(nameof(EditSummary));
        }
    }

    public bool IsCropped
    {
        get => _isCropped;
        private set
        {
            if (!SetProperty(ref _isCropped, value))
            {
                return;
            }

            OnPropertyChanged(nameof(EditSummary));
        }
    }

    public ImageSource? ThumbnailImage
    {
        get => _thumbnailImage;
        set
        {
            if (!SetProperty(ref _thumbnailImage, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ThumbnailWidth));
            OnPropertyChanged(nameof(ThumbnailHeight));
            OnPropertyChanged(nameof(CardWidth));
            OnPropertyChanged(nameof(IsThumbnailMissing));
        }
    }

    public string? RenderError
    {
        get => _renderError;
        set
        {
            if (!SetProperty(ref _renderError, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsThumbnailMissing));
            OnPropertyChanged(nameof(PreviewLabel));
        }
    }

    public bool IsThumbnailMissing => ThumbnailImage is null;

    public double ThumbnailLongEdge
    {
        get => _thumbnailLongEdge;
        set
        {
            if (Math.Abs(_thumbnailLongEdge - value) < 0.01)
            {
                return;
            }

            _thumbnailLongEdge = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ThumbnailWidth));
            OnPropertyChanged(nameof(ThumbnailHeight));
            OnPropertyChanged(nameof(CardWidth));
        }
    }

    public double ThumbnailWidth
    {
        get
        {
            var (width, height) = GetBitmapSize();
            if (width <= 0 || height <= 0)
            {
                return ThumbnailLongEdge * 0.75;
            }

            return width >= height
                ? ThumbnailLongEdge
                : ThumbnailLongEdge * width / height;
        }
    }

    public double ThumbnailHeight
    {
        get
        {
            var (width, height) = GetBitmapSize();
            if (width <= 0 || height <= 0)
            {
                return ThumbnailLongEdge;
            }

            return height >= width
                ? ThumbnailLongEdge
                : ThumbnailLongEdge * height / width;
        }
    }

    public double CardWidth => Math.Max(118, ThumbnailWidth + 24);

    public string PreviewLabel => RenderError is null
        ? $"Rendering page {PageNumber}..."
        : $"Preview unavailable\n{RenderError}";

    public string EditSummary
    {
        get
        {
            var parts = new List<string>();
            if (RotationDegrees != 0)
            {
                parts.Add($"{RotationDegrees}\u00B0");
            }

            if (IsCropped)
            {
                parts.Add("cropped");
            }

            return parts.Count == 0 ? "original" : string.Join(" \u00B7 ", parts);
        }
    }

    public void UpdateFrom(PdfEditPage page)
    {
        PageIndex = page.PageIndex;
        PageNumber = page.PageNumber;
        RotationDegrees = page.RotationDegrees;
        IsCropped = page.IsCropped;
    }

    public void InvalidateThumbnail()
    {
        ThumbnailImage = null;
        RenderError = null;
    }

    private (double Width, double Height) GetBitmapSize() =>
        ThumbnailImage is BitmapSource bitmap
            ? (bitmap.PixelWidth, bitmap.PixelHeight)
            : (0, 0);

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
