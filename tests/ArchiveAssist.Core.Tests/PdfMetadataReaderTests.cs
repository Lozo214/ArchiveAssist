using ArchiveAssist.Core.Services;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace ArchiveAssist.Core.Tests;

public sealed class PdfMetadataReaderTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ArchiveAssist-Metadata-{Guid.NewGuid():N}.pdf");

    [Fact]
    public void Read_ReturnsDocumentInfoAndFirstPageBoxes()
    {
        using (var document = new PdfDocument())
        {
            document.Info.Title = "Archive sample";
            document.Info.Author = "Archive Assist";
            var page = document.AddPage();
            page.Width = XUnit.FromInch(11);
            page.Height = XUnit.FromInch(17);
            page.CropBox = new PdfRectangle(new XPoint(0, 0), new XSize(8.5 * 72, 11 * 72));
            document.Save(_path);
        }

        var details = new PdfMetadataReader().Read(_path);

        Assert.Contains(details.Fields, field => field.Field == "Title" && field.Value == "Archive sample");
        Assert.Contains(details.Fields, field => field.Field == "Author" && field.Value == "Archive Assist");
        Assert.Contains(details.Fields, field => field.Field == "First page MediaBox" && field.Value == "11.00 x 17.00 in");
        Assert.Contains(details.Fields, field => field.Field == "First page CropBox" && field.Value == "8.50 x 11.00 in");
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
