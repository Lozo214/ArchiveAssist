using ArchiveAssist.Core.Models;

namespace ArchiveAssist.Core.Services;

public interface IPdfPageEqualizer
{
    Task<EqualizationPreview> PreviewAsync(
        string rootFolder,
        int maxPagesPerPdf,
        IProgress<EqualizationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<EqualizationResult> EqualizeAsync(
        string rootFolder,
        string outputRoot,
        int maxPagesPerPdf,
        IProgress<EqualizationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
