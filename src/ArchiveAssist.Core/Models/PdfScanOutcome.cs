namespace ArchiveAssist.Core.Models;

public sealed record ScanOptions(
    PageSizePreset Threshold,
    int MaxPagesPerPdf = 500,
    PdfQaMode? QaMode = null)
{
    public PdfQaMode EffectiveQaMode => QaMode ?? PdfQaMode.StandardQa;
}

public sealed record PdfScanSummary(
    int PdfCount,
    int TotalPages,
    int Documents,
    int Maps,
    int Photos,
    int PhotoBacks,
    int Total,
    int ErrorCount,
    int WarningCount,
    int FilesOverPageLimit,
    int SkippedFiles,
    long TotalFileSizeBytes,
    int PagesWithText,
    int PagesWithoutText,
    int FilesWithNonOcrPages,
    int NonSearchablePdfs);

public sealed record PdfScanOutcome(
    IReadOnlyList<ReportRow> Results,
    PdfScanSummary Summary,
    bool WasCancelled);

public sealed record PdfScanProgress(
    int CompletedFiles,
    int TotalFiles,
    string CurrentFileName,
    ReportRow Result);
