using ArchiveAssist.Core.Models;

namespace ArchiveAssist.Core.Services;

public interface IPdfFolderScanner
{
    Task<PdfDiscoveryResult> DiscoverAsync(
        string folderPath,
        CancellationToken cancellationToken = default);

    Task<PdfDiscoveryResult> DiscoverAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default);

    Task<PdfScanOutcome> ScanAsync(
        PdfDiscoveryResult discovery,
        ScanOptions options,
        IProgress<PdfScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
