using ArchiveAssist.Core.Models;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace ArchiveAssist.Core.Services;

public sealed class PdfMetadataReader : IPdfMetadataReader
{
    private const double PointsPerInch = 72d;

    public PdfMetadataDetails Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        using var document = PdfReader.Open(fullPath, PdfDocumentOpenMode.Import);
        var file = new FileInfo(fullPath);
        var fields = new List<DetailField>
        {
            new("File name", file.Name),
            new("Full path", fullPath),
            new("File size", $"{file.Length:N0} bytes"),
            new("PDF pages", document.PageCount.ToString("N0")),
            new("Encrypted", document.SecuritySettings.IsEncrypted ? "Yes" : "No"),
            new("PDF version", FormatPdfVersion(document.Version))
        };

        var info = document.Info;
        var metadataCount = 0;
        metadataCount += AddOptional(fields, "Title", info.Title);
        metadataCount += AddOptional(fields, "Author", info.Author);
        metadataCount += AddOptional(fields, "Subject", info.Subject);
        metadataCount += AddOptional(fields, "Keywords", info.Keywords);
        metadataCount += AddOptional(fields, "Creator", info.Creator);
        metadataCount += AddOptional(fields, "Producer", info.Producer);
        if (info.CreationDate != default)
        {
            fields.Add(new("Creation date", info.CreationDate.ToString("G")));
            metadataCount++;
        }
        if (info.ModificationDate != default)
        {
            fields.Add(new("Modification date", info.ModificationDate.ToString("G")));
            metadataCount++;
        }
        if (metadataCount == 0) fields.Add(new("Document metadata", "No document info metadata found"));

        if (document.PageCount > 0)
        {
            var page = document.Pages[0];
            fields.Add(new("First page MediaBox", FormatBox(page.MediaBoxReadOnly)));
            var cropBox = page.CropBoxReadOnly;
            fields.Add(new("First page CropBox", cropBox.IsZero ? FormatBox(page.MediaBoxReadOnly) : FormatBox(cropBox)));
        }

        return new(fields);
    }

    private static int AddOptional(ICollection<DetailField> fields, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        fields.Add(new(name, value));
        return 1;
    }

    private static string FormatBox(PdfRectangle rectangle)
    {
        var size = new PageSize(
            Math.Abs(rectangle.Width) / PointsPerInch,
            Math.Abs(rectangle.Height) / PointsPerInch);
        return size.ToString();
    }

    private static string FormatPdfVersion(int version) => version >= 10
        ? $"{version / 10}.{version % 10}"
        : version.ToString();
}
