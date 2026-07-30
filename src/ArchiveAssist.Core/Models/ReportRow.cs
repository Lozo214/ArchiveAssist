namespace ArchiveAssist.Core.Models;

public sealed class ReportRow(
    string fileName,
    string relativeFolder,
    string fullPath,
    ArchiveFileKind kind,
    long fileSizeBytes,
    int? pageCount,
    int documents,
    int maps,
    int photos,
    int photoBacks,
    PageSize? largestPageSize,
    bool overPageLimit,
    string? warning,
    string? error,
    PdfQaMode? qaMode = null,
    int ocrPagesChecked = 0,
    bool ocrCheckComplete = false,
    bool? searchableText = null,
    int pagesWithText = 0,
    int pagesWithoutText = 0,
    IReadOnlyList<int>? nonOcrPageNumbers = null)
{
    public string FileName { get; } = fileName;
    public string RelativeFolder { get; } = relativeFolder;
    public string FullPath { get; } = fullPath;
    public ArchiveFileKind Kind { get; } = kind;
    public long FileSizeBytes { get; } = fileSizeBytes;
    public int? PageCount { get; } = pageCount;
    public int Documents { get; } = documents;
    public int Maps { get; } = maps;
    public int Photos { get; } = photos;
    public int PhotoBacks { get; } = photoBacks;
    public int Total => Documents + Maps + Photos;
    public PageSize? LargestPageSize { get; } = largestPageSize;
    public bool OverPageLimit { get; } = overPageLimit;
    public string? Warning { get; } = warning;
    public string? Error { get; } = error;
    public PdfQaMode QaMode { get; } = qaMode ?? PdfQaMode.FastCountOnly;
    public int OcrPagesChecked { get; } = ocrPagesChecked;
    public bool OcrCheckComplete { get; } = ocrCheckComplete;
    public bool? SearchableText { get; } = searchableText;
    public int PagesWithText { get; } = pagesWithText;
    public int PagesWithoutText { get; } = pagesWithoutText;
    public IReadOnlyList<int> NonOcrPageNumbers { get; } = nonOcrPageNumbers ?? [];
    public bool IsSuccessful => Error is null;
    public bool HasWarning => Error is not null || Warning is not null || OverPageLimit;
    public bool IsLargeScan => Maps > 0;

    public string TypeLabel => Kind switch
    {
        ArchiveFileKind.Pdf => "PDF",
        ArchiveFileKind.Photo => "Photo",
        ArchiveFileKind.PhotoBack => "Photo Back",
        _ => "Skipped"
    };

    public string OverLimitLabel => Kind == ArchiveFileKind.Pdf ? (OverPageLimit ? "Yes" : "No") : string.Empty;
    public string OcrCheckStatusLabel => Kind != ArchiveFileKind.Pdf
        ? string.Empty
        : QaMode == PdfQaMode.FastCountOnly
            ? "Skipped"
            : SearchableText is null && OcrPagesChecked == 0
                ? "Unavailable"
            : OcrCheckComplete
                ? $"Complete ({OcrPagesChecked:N0} pages checked)"
                : $"Sampled ({OcrPagesChecked:N0} pages checked)";
    public string SearchableTextLabel => Kind != ArchiveFileKind.Pdf
        ? string.Empty
        : SearchableText is null
            ? QaMode == PdfQaMode.FastCountOnly ? "Not checked" : "Unknown"
            : QaMode == PdfQaMode.StandardQa && !OcrCheckComplete
                ? SearchableText.Value ? "Likely yes" : "Likely no"
                : SearchableText.Value ? "Yes" : "No";
    public string NonOcrPageNumbersLabel => string.Join(", ", NonOcrPageNumbers);
    public string IssuesLabel => string.Join("; ", new[] { Error, Warning }.Where(value => !string.IsNullOrWhiteSpace(value)));

    public string FileSizeLabel => FileSizeBytes switch
    {
        >= 1_073_741_824 => $"{FileSizeBytes / 1_073_741_824d:N1} GB",
        >= 1_048_576 => $"{FileSizeBytes / 1_048_576d:N1} MB",
        >= 1_024 => $"{FileSizeBytes / 1_024d:N1} KB",
        _ => $"{FileSizeBytes:N0} B"
    };

    public string LargestPageSizeLabel => LargestPageSize is null ? string.Empty : LargestPageSize.Value.ToString();

    public override string ToString() => $"{TypeLabel}: {FileName}";
}
