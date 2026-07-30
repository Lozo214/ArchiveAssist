using System.IO;
using System.Windows.Media.Imaging;
using PDFtoImage;
using SkiaSharp;

namespace ArchiveAssist.App.Services;

public sealed class PdfPageRenderer
{
    public BitmapImage RenderPage(byte[] pdfBytes, int pageIndex, int widthPixels)
    {
        var options = new RenderOptions
        {
            Width = widthPixels,
            WithAspectRatio = true
        };

        using var bitmap = Conversion.ToImage(
            pdfBytes,
            new Index(pageIndex),
            password: null,
            options);

        if (bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            throw new InvalidOperationException(
                $"The PDF renderer returned an empty image for page {pageIndex + 1}.");
        }

        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, quality: 92);
        if (encoded is null || encoded.Size == 0)
        {
            throw new InvalidOperationException(
                $"The PDF renderer could not create a preview for page {pageIndex + 1}.");
        }

        using var stream = new MemoryStream(encoded.ToArray());
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
