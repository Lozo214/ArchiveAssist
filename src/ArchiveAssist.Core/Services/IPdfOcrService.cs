using ArchiveAssist.Core.Models;

namespace ArchiveAssist.Core.Services;

public interface IPdfOcrService
{
    Task<OcrEngineStatus> CheckAvailabilityAsync(CancellationToken cancellationToken = default);

    Task<PdfOcrBatchResult> CreateSearchableCopiesAsync(
        PdfOcrRequest request,
        IProgress<PdfOcrProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
