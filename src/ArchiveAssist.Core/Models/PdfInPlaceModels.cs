namespace ArchiveAssist.Core.Models;

public enum PdfInPlaceOperation
{
    WholeFileOcr,
    Optimize
}

public enum PdfOptimizationLevel
{
    Lossless = 1,
    Balanced = 2,
    Aggressive = 3
}

public sealed record PdfInPlaceRequest(
    IReadOnlyList<string> Files,
    PdfInPlaceOperation Operation,
    PdfOptimizationLevel OptimizationLevel = PdfOptimizationLevel.Lossless);

public enum PdfInPlaceFileState
{
    Replaced,
    ReplacedWithWarning,
    Unchanged,
    Failed,
    Cancelled
}

public sealed record PdfInPlaceFileResult(
    string FileName,
    string FullPath,
    PdfInPlaceFileState State,
    int PageCount,
    int PagesWithoutText,
    long OriginalBytes,
    long FinalBytes,
    string Message,
    string? RecoveryPointId = null)
{
    public string StateLabel => State switch
    {
        PdfInPlaceFileState.Replaced => "Replaced",
        PdfInPlaceFileState.ReplacedWithWarning => "Replaced with warning",
        PdfInPlaceFileState.Unchanged => "Original kept",
        PdfInPlaceFileState.Failed => "Failed",
        _ => "Cancelled"
    };

    public long BytesSaved => Math.Max(0, OriginalBytes - FinalBytes);

    public double SavingsPercent =>
        OriginalBytes <= 0 ? 0 : BytesSaved * 100d / OriginalBytes;

    public string OriginalSizeLabel => FormatBytes(OriginalBytes);
    public string FinalSizeLabel => FormatBytes(FinalBytes);
    public string SavingsLabel => BytesSaved <= 0
        ? "-"
        : $"{FormatBytes(BytesSaved)} ({SavingsPercent:N1}%)";

    private static string FormatBytes(long bytes) =>
        bytes >= 1024 * 1024
            ? $"{bytes / (1024d * 1024d):N1} MB"
            : $"{bytes / 1024d:N1} KB";
}

public sealed record PdfInPlaceProgress(
    int CompletedFiles,
    int TotalFiles,
    string CurrentFileName,
    string Stage,
    PdfInPlaceFileResult? Result = null,
    double? CurrentFileProgress = null,
    int? CurrentPage = null,
    int? TotalPages = null,
    string? Detail = null);

public sealed record PdfInPlaceBatchResult(
    IReadOnlyList<PdfInPlaceFileResult> Results,
    bool WasCancelled)
{
    public int ReplacedCount => Results.Count(result =>
        result.State is PdfInPlaceFileState.Replaced or PdfInPlaceFileState.ReplacedWithWarning);
    public int WarningCount => Results.Count(result => result.State == PdfInPlaceFileState.ReplacedWithWarning);
    public int UnchangedCount => Results.Count(result => result.State == PdfInPlaceFileState.Unchanged);
    public int FailedCount => Results.Count(result => result.State == PdfInPlaceFileState.Failed);
    public long BytesSaved => Results.Sum(result => result.BytesSaved);
}
