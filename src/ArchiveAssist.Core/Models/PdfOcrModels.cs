namespace ArchiveAssist.Core.Models;

public sealed record OcrDependencyStatus(
    string Name,
    bool IsAvailable,
    string Version,
    string Details,
    bool IsRequired = true)
{
    public string StatusLabel => IsAvailable ? "Ready" : IsRequired ? "Missing" : "Optional";
}

public sealed record OcrEngineStatus(IReadOnlyList<OcrDependencyStatus> Dependencies)
{
    public bool IsReady => Dependencies.Count > 0 && Dependencies.All(item => !item.IsRequired || item.IsAvailable);

    public string Summary
    {
        get
        {
            var missingRequired = Dependencies.Where(item => item.IsRequired && !item.IsAvailable).ToList();
            if (missingRequired.Count > 0)
                return $"OCR setup needs attention: {string.Join(", ", missingRequired.Select(item => item.Name))}.";
            var missingOptional = Dependencies.Where(item => !item.IsRequired && !item.IsAvailable).ToList();
            return missingOptional.Count == 0
                ? "OCR setup is ready."
                : $"OCR setup is ready; optional component unavailable: {string.Join(", ", missingOptional.Select(item => item.Name))}.";
        }
    }
}

public enum PdfOcrPageMode
{
    MissingTextOnly,
    AllPages
}

public sealed record PdfOcrRequest(
    string SourceRoot,
    string OutputRoot,
    IReadOnlyList<ReportRow> Files,
    PdfOcrPageMode PageMode = PdfOcrPageMode.MissingTextOnly);

public enum PdfOcrFileState
{
    Completed,
    CompletedWithWarning,
    Skipped,
    Failed,
    Cancelled
}

public sealed record PdfOcrFileResult(
    string FileName,
    string SourcePath,
    string OutputPath,
    PdfOcrFileState State,
    int PageCount,
    int PagesWithText,
    int PagesWithoutText,
    string Message)
{
    public string StateLabel => State switch
    {
        PdfOcrFileState.Completed => "Completed",
        PdfOcrFileState.CompletedWithWarning => "Completed with warning",
        PdfOcrFileState.Skipped => "Skipped",
        PdfOcrFileState.Failed => "Failed",
        _ => "Cancelled"
    };
}

public sealed record PdfOcrProgress(
    int CompletedFiles,
    int TotalFiles,
    string CurrentFileName,
    string Stage,
    PdfOcrFileResult? Result = null);

public sealed record PdfOcrBatchResult(
    IReadOnlyList<PdfOcrFileResult> Results,
    bool WasCancelled)
{
    public int CompletedCount => Results.Count(result => result.State == PdfOcrFileState.Completed);
    public int WarningCount => Results.Count(result => result.State == PdfOcrFileState.CompletedWithWarning);
    public int SkippedCount => Results.Count(result => result.State == PdfOcrFileState.Skipped);
    public int FailedCount => Results.Count(result => result.State == PdfOcrFileState.Failed);
}

public sealed record PdfTextLayerInspection(
    int PageCount,
    int PagesWithText,
    int PagesWithoutText,
    IReadOnlyList<int> PagesWithoutTextNumbers);
