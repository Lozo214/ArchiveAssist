using ArchiveAssist.Core.Models;

namespace ArchiveAssist.Core.Services;

public interface IPdfTextLayerInspector
{
    Task<PdfTextLayerInspection> InspectAsync(
        string pdfPath,
        CancellationToken cancellationToken = default);
}
