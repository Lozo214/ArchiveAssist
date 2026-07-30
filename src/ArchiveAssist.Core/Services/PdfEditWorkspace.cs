using ArchiveAssist.Core.Models;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace ArchiveAssist.Core.Services;

/// <summary>
/// Holds an editable PDF in memory until an explicit save updates the opened source file.
/// </summary>
public sealed class PdfEditWorkspace : IDisposable
{
    private byte[]? _pdfBytes;
    private readonly IFileRecoveryService _recoveryService;
    private readonly int _recoveryRetentionDays;
    private FileRecoveryPoint? _sessionRecoveryPoint;
    private readonly List<PdfEditPage> _pages = [];

    public PdfEditWorkspace(
        IFileRecoveryService? recoveryService = null,
        int recoveryRetentionDays = 30)
    {
        _recoveryService = recoveryService ?? FileRecoveryService.CreateDefault();
        _recoveryRetentionDays = recoveryRetentionDays;
    }

    public IReadOnlyList<PdfEditPage> Pages => _pages;

    public string? SourcePath { get; private set; }

    public string? SessionBackupPath => _sessionRecoveryPoint?.BackupPath;

    public FileRecoveryPoint? SessionRecoveryPoint => _sessionRecoveryPoint;

    public bool HasDocument => _pdfBytes is not null && _pages.Count > 0;

    public long PdfByteLength => _pdfBytes?.LongLength ?? 0;

    public void Open(string pdfPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        var fullPath = Path.GetFullPath(pdfPath);
        var candidateBytes = File.ReadAllBytes(fullPath);
        using var document = OpenDocument(candidateBytes);
        var candidatePages = BuildPageList(document);

        SourcePath = fullPath;
        _pdfBytes = candidateBytes;
        _sessionRecoveryPoint = null;
        _pages.Clear();
        _pages.AddRange(candidatePages);
    }

    public void DeletePages(IEnumerable<int> pageIndexes)
    {
        ArgumentNullException.ThrowIfNull(pageIndexes);
        using var document = OpenDocument();
        var indexes = ValidIndexes(pageIndexes, document.PageCount)
            .OrderDescending()
            .ToList();

        if (indexes.Count == 0)
        {
            return;
        }

        if (indexes.Count >= document.PageCount)
        {
            throw new InvalidOperationException("A PDF must keep at least one page.");
        }

        foreach (var index in indexes)
        {
            document.Pages.RemoveAt(index);
        }

        Commit(document);

        foreach (var index in indexes)
        {
            _pages.RemoveAt(index);
        }

        RenumberPages();
    }

    /// <summary>
    /// Moves pages, preserving their relative order, to a slot in the list that remains
    /// after the moved pages have been removed.
    /// </summary>
    /// <returns>True when the page order changed.</returns>
    public bool ReorderPages(IEnumerable<int> pageIndexes, int insertionIndex)
    {
        ArgumentNullException.ThrowIfNull(pageIndexes);
        using var sourceDocument = OpenImportDocument();
        var indexes = ValidIndexes(pageIndexes, sourceDocument.PageCount)
            .Order()
            .ToList();

        if (indexes.Count == 0)
        {
            return false;
        }

        var movedIndexes = indexes.ToHashSet();
        var remainingIndexes = Enumerable.Range(0, sourceDocument.PageCount)
            .Where(index => !movedIndexes.Contains(index))
            .ToList();
        if (insertionIndex < 0 || insertionIndex > remainingIndexes.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(insertionIndex),
                "The page insertion position is outside the PDF.");
        }

        var reorderedIndexes = remainingIndexes.ToList();
        reorderedIndexes.InsertRange(insertionIndex, indexes);
        if (reorderedIndexes.SequenceEqual(Enumerable.Range(0, sourceDocument.PageCount)))
        {
            return false;
        }

        using var reorderedDocument = new PdfDocument();
        foreach (var index in reorderedIndexes)
        {
            reorderedDocument.AddPage(sourceDocument.Pages[index]);
        }

        Commit(reorderedDocument);
        var reorderedPages = reorderedIndexes
            .Select(index => _pages[index])
            .ToList();
        _pages.Clear();
        _pages.AddRange(reorderedPages);
        RenumberPages();
        return true;
    }

    public void RotatePages(IEnumerable<int> pageIndexes, int degrees)
    {
        ArgumentNullException.ThrowIfNull(pageIndexes);
        if (degrees == 0 || degrees % 90 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(degrees), "Rotation must be a non-zero multiple of 90 degrees.");
        }

        using var document = OpenDocument();
        var indexes = ValidIndexes(pageIndexes, document.PageCount).ToList();

        if (indexes.Count == 0)
        {
            return;
        }

        foreach (var index in indexes)
        {
            var page = document.Pages[index];
            page.Rotate = NormalizeRotation(page.Rotate + degrees);
        }

        Commit(document);

        foreach (var index in indexes)
        {
            _pages[index] = _pages[index] with
            {
                RotationDegrees = NormalizeRotation(_pages[index].RotationDegrees + degrees)
            };
        }
    }

    public void CropPages(
        IEnumerable<int> pageIndexes,
        double left,
        double top,
        double right,
        double bottom)
    {
        ArgumentNullException.ThrowIfNull(pageIndexes);
        ValidateCropMargin(left, nameof(left));
        ValidateCropMargin(top, nameof(top));
        ValidateCropMargin(right, nameof(right));
        ValidateCropMargin(bottom, nameof(bottom));

        using var document = OpenDocument();
        var indexes = ValidIndexes(pageIndexes, document.PageCount).ToList();

        if (indexes.Count == 0)
        {
            return;
        }

        foreach (var index in indexes)
        {
            var page = document.Pages[index];
            var currentBox = page.EffectiveCropBoxReadOnly;
            var newBox = new PdfRectangle(
                new XPoint(currentBox.X1 + left, currentBox.Y1 + bottom),
                new XPoint(currentBox.X2 - right, currentBox.Y2 - top));

            if (newBox.Width <= 0 || newBox.Height <= 0)
            {
                throw new InvalidOperationException(
                    $"The crop rectangle would remove all of page {index + 1}.");
            }

            page.CropBox = newBox;
        }

        Commit(document);

        foreach (var index in indexes)
        {
            _pages[index] = _pages[index] with { IsCropped = true };
        }
    }

    public (double Width, double Height) GetPageSize(int pageIndex)
    {
        using var document = OpenDocument();
        if (pageIndex < 0 || pageIndex >= document.PageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        var cropBox = document.Pages[pageIndex].EffectiveCropBoxReadOnly;
        return (cropBox.Width, cropBox.Height);
    }

    public byte[] GetPdfBytesSnapshot() =>
        _pdfBytes?.ToArray()
        ?? throw new InvalidOperationException("Open a PDF before editing pages.");

    public PdfEditWorkspaceSnapshot CreateSnapshot() =>
        new(
            GetPdfBytesSnapshot(),
            _pages.Select(page => page with { }).ToList());

    public void RestoreSnapshot(PdfEditWorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _pdfBytes = snapshot.PdfBytes.ToArray();
        _pages.Clear();
        _pages.AddRange(snapshot.Pages.Select(page => page with { }));
    }

    /// <summary>
    /// Replaces the opened PDF with the working document after preserving the original once.
    /// </summary>
    /// <returns>The managed recovery-file path created for this editing session.</returns>
    public string Save()
    {
        var bytes = GetPdfBytesSnapshot();
        var sourcePath = SourcePath
            ?? throw new InvalidOperationException("Open a PDF before saving changes.");

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException(
                "The opened PDF is no longer available at its original location.",
                sourcePath);
        }

        _sessionRecoveryPoint ??= _recoveryService.CreateRecoveryPoint(
            sourcePath,
            "PDF Editor",
            _recoveryRetentionDays);
        WriteAtomically(sourcePath, bytes);
        return _sessionRecoveryPoint.BackupPath;
    }

    /// <summary>
    /// Restores the original session recovery point after first preserving the currently saved PDF.
    /// </summary>
    public PdfBackupRestoreResult RestoreSessionBackup()
    {
        var sourcePath = SourcePath
            ?? throw new InvalidOperationException("Open a PDF before restoring a backup.");
        var recoveryPoint = _sessionRecoveryPoint;
        var backupPath = recoveryPoint?.BackupPath;
        if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
        {
            throw new InvalidOperationException(
                "No editing-session recovery point is available for this PDF.");
        }

        var restoreResult = _recoveryService.Restore(
            recoveryPoint!.Id,
            _recoveryRetentionDays);
        var restoredBytes = File.ReadAllBytes(sourcePath);
        using var restoredDocument = OpenDocument(restoredBytes);
        var restoredPages = BuildPageList(restoredDocument);
        _pdfBytes = restoredBytes;
        _pages.Clear();
        _pages.AddRange(restoredPages);

        return new(
            backupPath,
            restoreResult.PreservedCurrentPoint?.BackupPath ?? string.Empty);
    }

    /// <summary>
    /// Writes a copy of the working PDF. The opened source path is explicitly rejected.
    /// </summary>
    public void SaveCopy(string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var bytes = GetPdfBytesSnapshot();
        var fullOutputPath = Path.GetFullPath(outputPath);

        if (SourcePath is not null &&
            string.Equals(fullOutputPath, SourcePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "This editor cannot replace the opened PDF. Choose a different name or folder.");
        }

        WriteAtomically(fullOutputPath, bytes);
    }

    /// <summary>
    /// Writes a copy of the working PDF and makes that copy the active document.
    /// </summary>
    public void SaveCopyAndContinue(string outputPath)
    {
        SaveCopy(outputPath);
        SourcePath = Path.GetFullPath(outputPath);
        _sessionRecoveryPoint = null;
    }

    public void Dispose()
    {
        _pdfBytes = null;
        _pages.Clear();
        SourcePath = null;
        _sessionRecoveryPoint = null;
    }

    private PdfDocument OpenDocument() =>
        OpenDocument(
            _pdfBytes
            ?? throw new InvalidOperationException("Open a PDF before editing pages."));

    private PdfDocument OpenImportDocument() =>
        OpenImportDocument(
            _pdfBytes
            ?? throw new InvalidOperationException("Open a PDF before editing pages."));

    private static PdfDocument OpenDocument(byte[] pdfBytes)
    {
        var stream = new MemoryStream(pdfBytes, writable: false);

        try
        {
            return PdfReader.Open(stream, PdfDocumentOpenMode.Modify);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static PdfDocument OpenImportDocument(byte[] pdfBytes)
    {
        var stream = new MemoryStream(pdfBytes, writable: false);

        try
        {
            return PdfReader.Open(stream, PdfDocumentOpenMode.Import);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private void Commit(PdfDocument document)
    {
        using var stream = new MemoryStream();
        document.Save(stream, closeStream: false);
        _pdfBytes = stream.ToArray();
    }

    private static List<PdfEditPage> BuildPageList(PdfDocument document)
    {
        var pages = new List<PdfEditPage>(document.PageCount);

        for (var index = 0; index < document.PageCount; index++)
        {
            var page = document.Pages[index];
            pages.Add(new(
                Guid.NewGuid(),
                index,
                index + 1,
                NormalizeRotation(page.Rotate),
                page.HasCropBox));
        }

        return pages;
    }

    private void RenumberPages()
    {
        for (var index = 0; index < _pages.Count; index++)
        {
            _pages[index] = _pages[index] with
            {
                PageIndex = index,
                PageNumber = index + 1
            };
        }
    }

    private static IEnumerable<int> ValidIndexes(IEnumerable<int> pageIndexes, int pageCount) =>
        pageIndexes
            .Distinct()
            .Where(index => index >= 0 && index < pageCount);

    private static void ValidateCropMargin(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Crop margins must be finite, non-negative values.");
        }
    }

    private static int NormalizeRotation(int degrees)
    {
        var normalized = degrees % 360;
        return normalized < 0 ? normalized + 360 : normalized;
    }

    private static void WriteAtomically(string outputPath, byte[] bytes)
    {
        var outputDirectory = Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException("The output path does not have a parent folder.");
        Directory.CreateDirectory(outputDirectory);
        var temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.part");

        try
        {
            File.WriteAllBytes(temporaryPath, bytes);
            File.Move(temporaryPath, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
