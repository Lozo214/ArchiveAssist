using ArchiveAssist.Core.Models;

namespace ArchiveAssist.Core.Services;

public interface IPdfInPlaceService
{
    Task<OcrEngineStatus> CheckInPlaceAvailabilityAsync(
        PdfInPlaceOperation operation,
        CancellationToken cancellationToken = default);

    Task<PdfInPlaceBatchResult> ProcessInPlaceAsync(
        PdfInPlaceRequest request,
        IProgress<PdfInPlaceProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
