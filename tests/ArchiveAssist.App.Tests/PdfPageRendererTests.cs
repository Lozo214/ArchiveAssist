using ArchiveAssist.App.Services;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using System.IO;

namespace ArchiveAssist.App.Tests;

public sealed class PdfPageRendererTests
{
    [Fact]
    public void RenderPageCreatesFrozenWpfImage()
    {
        var image = new PdfPageRenderer().RenderPage(
            CreatePdfBytes(1),
            pageIndex: 0,
            widthPixels: 400);

        Assert.Equal(400, image.PixelWidth);
        Assert.True(image.PixelHeight > image.PixelWidth);
        Assert.True(image.IsFrozen);
    }

    [Fact]
    public async Task RenderPageSupportsConcurrentThumbnailAndDetailRequests()
    {
        var pdfBytes = CreatePdfBytes(3);
        var renderer = new PdfPageRenderer();

        var images = await Task.WhenAll(
            Task.Run(() => renderer.RenderPage(pdfBytes, 0, 400)),
            Task.Run(() => renderer.RenderPage(pdfBytes, 1, 560)),
            Task.Run(() => renderer.RenderPage(pdfBytes, 2, 1200)));

        Assert.Equal([400, 560, 1200], images.Select(image => image.PixelWidth));
        Assert.All(images, image => Assert.True(image.IsFrozen));
    }

    private static byte[] CreatePdfBytes(int pageCount)
    {
        using var document = new PdfDocument();
        for (var index = 0; index < pageCount; index++)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(600);
            page.Height = XUnit.FromPoint(800);
            using var graphics = XGraphics.FromPdfPage(page);
            graphics.DrawRectangle(XPens.Black, new XRect(50, 50, 500, 700));
        }

        using var stream = new MemoryStream();
        document.Save(stream, closeStream: false);
        return stream.ToArray();
    }
}
