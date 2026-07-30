using ArchiveAssist.Core.Models;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;

namespace ArchiveAssist.Core.Services;

public sealed class PdfFolderScanner : IPdfFolderScanner
{
    private const string GeneratedFolderName = "Equalized PDFs";
    private const double PointsPerInch = 72d;
    private const int StandardQaFirstPageCount = 7;

    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".img", ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp", ".gif",
        ".webp", ".heic", ".heif", ".dng", ".cr2", ".cr3", ".nef", ".arw"
    };

    public Task<PdfDiscoveryResult> DiscoverAsync(
        string folderPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        var root = Path.GetFullPath(folderPath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Folder not found: {root}");
        }

        return DiscoverAsync([root], cancellationToken);
    }

    public Task<PdfDiscoveryResult> DiscoverAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0) throw new ArgumentException("Select at least one file or folder.", nameof(paths));

        var selectedPaths = NormalizeSelection(paths);
        var root = FindCommonRoot(selectedPaths);
        return Task.Run(() => Discover(root, selectedPaths, cancellationToken), cancellationToken);
    }

    public Task<PdfScanOutcome> ScanAsync(
        PdfDiscoveryResult discovery,
        ScanOptions options,
        IProgress<PdfScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(options);

        return Task.Run(() =>
        {
            var results = new List<ReportRow>();
            foreach (var file in discovery.Files)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return CreateOutcome(results, wasCancelled: true);
                }

                ReportRow result;
                try
                {
                    result = Inspect(file, options, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return CreateOutcome(results, wasCancelled: true);
                }
                results.Add(result);
                progress?.Report(new(results.Count, discovery.Files.Count, file.FileName, result));
            }

            return CreateOutcome(results, wasCancelled: false);
        });
    }

    private static PdfDiscoveryResult Discover(
        string root,
        IReadOnlyList<string> selectedPaths,
        CancellationToken cancellationToken)
    {
        var files = new List<DiscoveredFile>();
        var warnings = new List<string>();
        var discoveredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var selectedPath in selectedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(selectedPath))
            {
                AddDiscoveredFile(root, selectedPath, files, discoveredPaths);
                continue;
            }

            var pending = new Stack<string>();
            pending.Push(selectedPath);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = pending.Pop();

                foreach (var file in TryGetFiles(current, warnings))
                {
                    AddDiscoveredFile(root, file, files, discoveredPaths);
                }

                foreach (var directory in TryGetDirectories(current, warnings))
                {
                    if (string.Equals(Path.GetFileName(directory), GeneratedFolderName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                        {
                            pending.Push(directory);
                        }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        warnings.Add($"Could not inspect folder '{directory}': {exception.Message}");
                    }
                }
            }
        }

        files.Sort((left, right) =>
        {
            var kindComparison = left.Kind.CompareTo(right.Kind);
            return kindComparison != 0
                ? kindComparison
                : StringComparer.OrdinalIgnoreCase.Compare(left.FullPath, right.FullPath);
        });
        return new(root, files, warnings, selectedPaths);
    }

    private static void AddDiscoveredFile(
        string root,
        string path,
        ICollection<DiscoveredFile> files,
        ISet<string> discoveredPaths)
    {
        var file = Path.GetFullPath(path);
        if (!discoveredPaths.Add(file)) return;

        var extension = Path.GetExtension(file);
        var relativeFolder = Path.GetDirectoryName(Path.GetRelativePath(root, file));
        files.Add(new(
            file,
            Path.GetFileName(file),
            string.IsNullOrEmpty(relativeFolder) ? "." : relativeFolder,
            string.IsNullOrEmpty(extension) ? "[no extension]" : extension.ToLowerInvariant(),
            ClassifyFile(file, extension)));
    }

    private static IReadOnlyList<string> NormalizeSelection(IEnumerable<string> paths)
    {
        var uniquePaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (uniquePaths.Count == 0) throw new ArgumentException("Select at least one file or folder.", nameof(paths));

        var missing = uniquePaths.FirstOrDefault(path => !File.Exists(path) && !Directory.Exists(path));
        if (missing is not null) throw new FileNotFoundException($"Selected file or folder was not found: {missing}", missing);

        var selectedFolders = uniquePaths
            .Where(Directory.Exists)
            .Where(candidate => !uniquePaths.Any(other =>
                !string.Equals(candidate, other, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(other) && IsSameOrNested(candidate, other)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var selectedFiles = uniquePaths
            .Where(File.Exists)
            .Where(file => !selectedFolders.Any(folder => IsSameOrNested(file, folder)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

        return [.. selectedFolders, .. selectedFiles];
    }

    private static string FindCommonRoot(IReadOnlyList<string> paths)
    {
        var containers = paths
            .Select(path => Directory.Exists(path) ? path : Path.GetDirectoryName(path)!)
            .ToList();
        var root = Path.TrimEndingDirectorySeparator(containers[0]);

        foreach (var container in containers.Skip(1))
        {
            if (!string.Equals(Path.GetPathRoot(root), Path.GetPathRoot(container), StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetDirectoryName(paths[0]) ?? Path.GetPathRoot(paths[0]) ?? paths[0];
            }

            while (!IsSameOrNested(container, root))
            {
                var parent = Directory.GetParent(root)?.FullName;
                if (parent is null) break;
                root = parent;
            }
        }

        return root;
    }

    private static bool IsSameOrNested(string candidate, string root)
    {
        var fullCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(fullCandidate, fullRoot, StringComparison.OrdinalIgnoreCase) ||
               fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static ArchiveFileKind ClassifyFile(string path, string extension)
    {
        if (string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return ArchiveFileKind.Pdf;
        }

        if (!PhotoExtensions.Contains(extension))
        {
            return ArchiveFileKind.Skipped;
        }

        var name = Path.GetFileNameWithoutExtension(path).TrimEnd();
        return name.EndsWith("_back", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("_b", StringComparison.OrdinalIgnoreCase)
            ? ArchiveFileKind.PhotoBack
            : ArchiveFileKind.Photo;
    }

    private static string[] TryGetFiles(string folder, List<string> warnings)
    {
        try
        {
            return Directory.GetFiles(folder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not read files in '{folder}': {exception.Message}");
            return [];
        }
    }

    private static string[] TryGetDirectories(string folder, List<string> warnings)
    {
        try
        {
            return Directory.GetDirectories(folder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not read subfolders in '{folder}': {exception.Message}");
            return [];
        }
    }

    private static ReportRow Inspect(
        DiscoveredFile file,
        ScanOptions options,
        CancellationToken cancellationToken)
    {
        var fileSize = GetFileSize(file.FullPath);
        return file.Kind switch
        {
            ArchiveFileKind.Pdf => InspectPdf(file, fileSize, options, cancellationToken),
            ArchiveFileKind.Photo => CreateNonPdfRow(file, fileSize, photos: 1, photoBacks: 0),
            ArchiveFileKind.PhotoBack => CreateNonPdfRow(file, fileSize, photos: 0, photoBacks: 1),
            _ => CreateSkippedRow(file, fileSize)
        };
    }

    private static ReportRow InspectPdf(
        DiscoveredFile file,
        long fileSize,
        ScanOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            var documents = 0;
            var maps = 0;
            PageSize? largestPage = null;
            int pageCount;

            using (var document = PdfReader.Open(file.FullPath, PdfDocumentOpenMode.Import))
            {
                pageCount = document.PageCount;
                foreach (PdfPage page in document.Pages)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var mediaSize = ToPageSize(page.MediaBoxReadOnly);
                    var cropBox = page.CropBoxReadOnly;
                    var visibleSize = cropBox.IsZero ? mediaSize : ToPageSize(cropBox);

                    if (options.Threshold.IsMap(visibleSize)) maps++;
                    else documents++;

                    if (largestPage is null || visibleSize.Area > largestPage.Value.Area) largestPage = visibleSize;
                }
            }

            var overLimit = pageCount > options.MaxPagesPerPdf;
            var warnings = new List<string>();
            if (overLimit) warnings.Add($"Page count exceeds limit ({pageCount:N0} > {options.MaxPagesPerPdf:N0})");

            var textInspection = InspectPdfText(file.FullPath, options.EffectiveQaMode, cancellationToken);
            if (!string.IsNullOrWhiteSpace(textInspection.Warning)) warnings.Add(textInspection.Warning);

            return new(file.FileName, file.RelativeFolder, file.FullPath, file.Kind, fileSize,
                pageCount, documents, maps, 0, 0, largestPage, overLimit,
                warnings.Count == 0 ? null : string.Join("; ", warnings), null,
                options.EffectiveQaMode, textInspection.PagesChecked, textInspection.CheckComplete,
                textInspection.SearchableText, textInspection.PagesWithText, textInspection.PagesWithoutText,
                textInspection.NonOcrPageNumbers);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(file.FileName, file.RelativeFolder, file.FullPath, file.Kind, fileSize,
                null, 0, 0, 0, 0, null, false, null,
                $"Could not open PDF: {exception.Message}", options.EffectiveQaMode);
        }
    }

    private static PdfTextInspection InspectPdfText(
        string path,
        PdfQaMode qaMode,
        CancellationToken cancellationToken)
    {
        if (qaMode == PdfQaMode.FastCountOnly)
        {
            return new(0, false, null, 0, 0, [], null);
        }

        try
        {
            using var document = PdfPigDocument.Open(path);
            return qaMode == PdfQaMode.DeepOcrCheck
                ? InspectAllPageText(document, cancellationToken)
                : InspectSampledPageText(document, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new(0, false, null, 0, 0, [],
                $"Could not inspect searchable text: {exception.Message}");
        }
    }

    private static PdfTextInspection InspectAllPageText(
        PdfPigDocument document,
        CancellationToken cancellationToken)
    {
        var pagesWithText = 0;
        var pagesWithoutText = 0;
        var nonOcrPageNumbers = new List<int>();

        for (var pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PageContainsText(document, pageNumber))
            {
                pagesWithText++;
            }
            else
            {
                pagesWithoutText++;
                nonOcrPageNumbers.Add(pageNumber);
            }
        }

        return new(
            document.NumberOfPages,
            true,
            pagesWithoutText == 0,
            pagesWithText,
            pagesWithoutText,
            nonOcrPageNumbers,
            pagesWithoutText == 0 ? null : "Pages without searchable text found");
    }

    private static PdfTextInspection InspectSampledPageText(
        PdfPigDocument document,
        CancellationToken cancellationToken)
    {
        var pagesChecked = 0;
        foreach (var zeroBasedPageIndex in SamplePageIndexes(document.NumberOfPages))
        {
            cancellationToken.ThrowIfCancellationRequested();
            pagesChecked++;
            if (PageContainsText(document, zeroBasedPageIndex + 1))
            {
                return new(pagesChecked, false, true, 1, 0, [], null);
            }
        }

        return new(pagesChecked, false, false, 0, 0, [],
            "No searchable text found in sampled pages");
    }

    private static IReadOnlyList<int> SamplePageIndexes(int pageCount)
    {
        if (pageCount <= 0) return [];
        var indexes = Enumerable.Range(0, Math.Min(pageCount, StandardQaFirstPageCount)).ToHashSet();
        indexes.Add(pageCount / 2);
        indexes.Add(pageCount - 1);
        return indexes.Order().ToList();
    }

    private static bool PageContainsText(PdfPigDocument document, int pageNumber)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(
                ContentOrderTextExtractor.GetText(document.GetPage(pageNumber)));
        }
        catch
        {
            return false;
        }
    }

    private static ReportRow CreateNonPdfRow(DiscoveredFile file, long fileSize, int photos, int photoBacks) =>
        new(file.FileName, file.RelativeFolder, file.FullPath, file.Kind, fileSize,
            null, photoBacks, 0, photos, photoBacks, null, false, null, null);

    private static ReportRow CreateSkippedRow(DiscoveredFile file, long fileSize) =>
        new(file.FileName, file.RelativeFolder, file.FullPath, file.Kind, fileSize,
            null, 0, 0, 0, 0, null, false,
            $"Skipped: unsupported file type ({file.Extension})", null);

    private static PageSize ToPageSize(PdfRectangle rectangle) => new(
        Math.Abs(rectangle.Width) / PointsPerInch,
        Math.Abs(rectangle.Height) / PointsPerInch);

    private static long GetFileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static PdfScanOutcome CreateOutcome(IReadOnlyList<ReportRow> results, bool wasCancelled)
    {
        var summary = new PdfScanSummary(
            results.Count(row => row.Kind == ArchiveFileKind.Pdf),
            results.Sum(row => row.PageCount ?? 0),
            results.Sum(row => row.Documents),
            results.Sum(row => row.Maps),
            results.Sum(row => row.Photos),
            results.Sum(row => row.PhotoBacks),
            results.Sum(row => row.Total),
            results.Count(row => !row.IsSuccessful),
            results.Count(row => row.Kind != ArchiveFileKind.Skipped && row.HasWarning),
            results.Count(row => row.OverPageLimit),
            results.Count(row => row.Kind == ArchiveFileKind.Skipped),
            results.Sum(row => row.FileSizeBytes),
            results.Sum(row => row.PagesWithText),
            results.Sum(row => row.PagesWithoutText),
            results.Count(row => row.PagesWithoutText > 0),
            results.Count(row => row.SearchableText is false));

        return new(results, summary, wasCancelled);
    }

    private sealed record PdfTextInspection(
        int PagesChecked,
        bool CheckComplete,
        bool? SearchableText,
        int PagesWithText,
        int PagesWithoutText,
        IReadOnlyList<int> NonOcrPageNumbers,
        string? Warning);
}
