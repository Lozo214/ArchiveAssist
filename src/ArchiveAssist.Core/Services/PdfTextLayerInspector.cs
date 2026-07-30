using ArchiveAssist.Core.Models;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;

namespace ArchiveAssist.Core.Services;

public sealed class PdfTextLayerInspector : IPdfTextLayerInspector
{
    public Task<PdfTextLayerInspection> InspectAsync(
        string pdfPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        return Task.Run(() => Inspect(pdfPath, cancellationToken), cancellationToken);
    }

    private static PdfTextLayerInspection Inspect(string pdfPath, CancellationToken cancellationToken)
    {
        using var document = PdfPigDocument.Open(pdfPath);
        var pagesWithText = 0;
        var pagesWithoutText = new List<int>();

        for (var pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string text;
            try
            {
                text = ContentOrderTextExtractor.GetText(document.GetPage(pageNumber));
            }
            catch
            {
                text = string.Empty;
            }

            if (string.IsNullOrWhiteSpace(text)) pagesWithoutText.Add(pageNumber);
            else pagesWithText++;
        }

        return new(
            document.NumberOfPages,
            pagesWithText,
            pagesWithoutText.Count,
            pagesWithoutText);
    }
}
