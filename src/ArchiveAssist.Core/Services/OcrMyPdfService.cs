using ArchiveAssist.Core.Models;
using System.Text.RegularExpressions;

namespace ArchiveAssist.Core.Services;

public sealed class OcrMyPdfService : IPdfOcrService, IPdfInPlaceService
{
    private const string OcrMyPdfName = "OCRmyPDF";
    private const string TesseractName = "Tesseract";
    private const string GhostscriptName = "Ghostscript";
    private static readonly Regex LeadingPageNumberPattern =
        new(@"^\s*(?<page>\d+)\s+", RegexOptions.Compiled);
    private static readonly Regex FractionalPagePattern =
        new(@"\b(?<page>\d+)\s*/\s*(?<total>\d+)\b", RegexOptions.Compiled);
    private static readonly Regex AnsiEscapePattern =
        new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);

    private readonly IOcrProcessRunner _processRunner;
    private readonly IPdfTextLayerInspector _textInspector;
    private readonly IFileRecoveryService _recoveryService;
    private readonly int _recoveryRetentionDays;
    private OcrCommand? _ocrCommand;

    public OcrMyPdfService(
        IOcrProcessRunner? processRunner = null,
        IPdfTextLayerInspector? textInspector = null,
        IFileRecoveryService? recoveryService = null,
        int recoveryRetentionDays = 30)
    {
        _processRunner = processRunner ?? new OcrProcessRunner();
        _textInspector = textInspector ?? new PdfTextLayerInspector();
        _recoveryService = recoveryService ?? FileRecoveryService.CreateDefault();
        _recoveryRetentionDays = recoveryRetentionDays;
    }

    public async Task<OcrEngineStatus> CheckAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        var ocrTask = ResolveOcrMyPdfAsync(cancellationToken);
        var tesseractTask = ProbeFirstAsync(
            TesseractName,
            BuildTesseractCandidates(),
            ["--version"],
            "Install 64-bit Tesseract OCR.",
            cancellationToken);
        var ghostscriptTask = ProbeFirstAsync(
            GhostscriptName,
            BuildGhostscriptCandidates(),
            ["--version"],
            "Optional for standard PDF output; install 64-bit Ghostscript if PDF/A output is added later.",
            cancellationToken,
            isRequired: false);

        var resolvedOcr = await ocrTask;
        _ocrCommand = resolvedOcr.Command;
        var dependencies = new[]
        {
            resolvedOcr.Status,
            await tesseractTask,
            await ghostscriptTask
        };
        return new(dependencies);
    }

    public async Task<OcrEngineStatus> CheckInPlaceAvailabilityAsync(
        PdfInPlaceOperation operation,
        CancellationToken cancellationToken = default)
    {
        if (operation == PdfInPlaceOperation.WholeFileOcr)
            return await CheckAvailabilityAsync(cancellationToken);

        if (operation != PdfInPlaceOperation.Optimize)
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported in-place operation.");

        var resolvedOcr = await ResolveOcrMyPdfAsync(cancellationToken);
        _ocrCommand = resolvedOcr.Command;
        return new([resolvedOcr.Status]);
    }

    public async Task<PdfOcrBatchResult> CreateSearchableCopiesAsync(
        PdfOcrRequest request,
        IProgress<PdfOcrProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sourceRoot = RequireExistingDirectory(request.SourceRoot, "Source folder");
        var outputRoot = RequireExistingDirectory(request.OutputRoot, "Output folder");
        EnsureSeparateRoots(sourceRoot, outputRoot);

        var status = await CheckAvailabilityAsync(cancellationToken);
        if (!status.IsReady || _ocrCommand is null)
            throw new InvalidOperationException(status.Summary);

        var files = request.Files
            .Where(row => row.Kind == ArchiveFileKind.Pdf && row.IsSuccessful)
            .DistinctBy(row => row.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0) throw new InvalidOperationException("No readable PDF rows were selected for OCR.");

        var results = new List<PdfOcrFileResult>();
        for (var index = 0; index < files.Count; index++)
        {
            var row = files[index];
            if (cancellationToken.IsCancellationRequested)
                return new(results, WasCancelled: true);

            var outputPath = BuildOutputPath(sourceRoot, outputRoot, row);
            progress?.Report(new(index, files.Count, row.FileName, "Preparing"));

            if (File.Exists(outputPath))
            {
                var skipped = new PdfOcrFileResult(
                    row.FileName, row.FullPath, outputPath, PdfOcrFileState.Skipped,
                    row.PageCount ?? 0, 0, 0, "Output already exists; nothing was overwritten.");
                results.Add(skipped);
                progress?.Report(new(results.Count, files.Count, row.FileName, "Skipped", skipped));
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            var temporaryPath = outputPath + $".{Guid.NewGuid():N}.archiveassist.part.pdf";
            try
            {
                progress?.Report(new(index, files.Count, row.FileName, "Running OCR"));
                var processResult = await RunOcrAsync(
                    row.FullPath,
                    temporaryPath,
                    request.PageMode,
                    cancellationToken);
                if (processResult.ExitCode != 0 || !File.Exists(temporaryPath))
                {
                    var message = FirstUsefulLine(processResult.StandardError, processResult.StandardOutput) ??
                                  $"OCRmyPDF exited with code {processResult.ExitCode}.";
                    var failed = Failed(row, outputPath, message);
                    results.Add(failed);
                    progress?.Report(new(results.Count, files.Count, row.FileName, "Failed", failed));
                    continue;
                }

                progress?.Report(new(index, files.Count, row.FileName, "Verifying searchable copy"));
                var inspection = await _textInspector.InspectAsync(temporaryPath, cancellationToken);
                if (row.PageCount is { } expectedPages && inspection.PageCount != expectedPages)
                {
                    var failed = Failed(
                        row,
                        outputPath,
                        $"Verification failed: expected {expectedPages:N0} pages but found {inspection.PageCount:N0}.");
                    results.Add(failed);
                    progress?.Report(new(results.Count, files.Count, row.FileName, "Failed verification", failed));
                    continue;
                }

                File.Move(temporaryPath, outputPath, overwrite: false);
                var hasUnsearchablePages = inspection.PagesWithoutText > 0;
                var completed = new PdfOcrFileResult(
                    row.FileName,
                    row.FullPath,
                    outputPath,
                    hasUnsearchablePages ? PdfOcrFileState.CompletedWithWarning : PdfOcrFileState.Completed,
                    inspection.PageCount,
                    inspection.PagesWithText,
                    inspection.PagesWithoutText,
                    hasUnsearchablePages
                        ? $"Searchable copy created; pages still without extractable text: {string.Join(", ", inspection.PagesWithoutTextNumbers)}."
                        : "Searchable copy created and verified.");
                results.Add(completed);
                progress?.Report(new(results.Count, files.Count, row.FileName, "Completed", completed));
            }
            catch (OperationCanceledException)
            {
                TryDelete(temporaryPath);
                var cancelled = new PdfOcrFileResult(
                    row.FileName, row.FullPath, outputPath, PdfOcrFileState.Cancelled,
                    row.PageCount ?? 0, 0, 0, "OCR was cancelled; no partial output was kept.");
                results.Add(cancelled);
                progress?.Report(new(results.Count, files.Count, row.FileName, "Cancelled", cancelled));
                return new(results, WasCancelled: true);
            }
            catch (Exception exception)
            {
                var failed = Failed(row, outputPath, exception.Message);
                results.Add(failed);
                progress?.Report(new(results.Count, files.Count, row.FileName, "Failed", failed));
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }

        return new(results, WasCancelled: false);
    }

    public async Task<PdfInPlaceBatchResult> ProcessInPlaceAsync(
        PdfInPlaceRequest request,
        IProgress<PdfInPlaceProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var files = request.Files
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Where(path => string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0)
            throw new InvalidOperationException("Select at least one PDF file.");

        var status = await CheckInPlaceAvailabilityAsync(request.Operation, cancellationToken);
        if (!status.IsReady || _ocrCommand is null)
            throw new InvalidOperationException(status.Summary);

        var results = new List<PdfInPlaceFileResult>();
        for (var index = 0; index < files.Count; index++)
        {
            var sourcePath = files[index];
            var fileName = Path.GetFileName(sourcePath);
            if (cancellationToken.IsCancellationRequested)
                return new(results, WasCancelled: true);

            progress?.Report(new(index, files.Count, fileName, "Inspecting original"));
            if (!File.Exists(sourcePath))
            {
                var missing = FailedInPlace(sourcePath, 0, 0, "The PDF no longer exists.");
                results.Add(missing);
                progress?.Report(new(results.Count, files.Count, fileName, "Failed", missing));
                continue;
            }

            var originalBytes = new FileInfo(sourcePath).Length;
            var temporaryPath = BuildTemporaryPath(sourcePath, "part");
            try
            {
                var originalInspection = await _textInspector.InspectAsync(sourcePath, cancellationToken);
                var totalPages = originalInspection.PageCount;
                var runningStage = request.Operation == PdfInPlaceOperation.WholeFileOcr
                    ? "OCRing every page"
                    : "Optimizing";
                progress?.Report(new(
                    index,
                    files.Count,
                    fileName,
                    runningStage,
                    CurrentFileProgress: null,
                    TotalPages: totalPages,
                    Detail: "Starting OCRmyPDF..."));

                var lastReportedPage = 0;
                var liveOutput = new InlineProgress<OcrProcessOutput>(output =>
                {
                    var currentPage = TryReadPageNumber(output.Line, totalPages);
                    if (currentPage is { } parsedPage)
                    {
                        lastReportedPage = Math.Max(lastReportedPage, parsedPage);
                        currentPage = lastReportedPage;
                    }
                    var detail = FriendlyOcrDetail(output.Line);
                    if (currentPage is null && detail is null) return;

                    var stage = currentPage is { } page
                        ? request.Operation == PdfInPlaceOperation.WholeFileOcr
                            ? $"OCRing page {page:N0} of {totalPages:N0}"
                            : $"Optimizing page {page:N0} of {totalPages:N0}"
                        : runningStage;
                    var fileProgress = currentPage is { } pageNumber && totalPages > 0
                        ? Math.Clamp(pageNumber / (double)totalPages * 0.85, 0.02, 0.85)
                        : (double?)null;
                    progress?.Report(new(
                        index,
                        files.Count,
                        fileName,
                        stage,
                        CurrentFileProgress: fileProgress,
                        CurrentPage: currentPage,
                        TotalPages: totalPages,
                        Detail: detail));
                });

                var processResult = await RunInPlaceAsync(
                    request,
                    sourcePath,
                    temporaryPath,
                    liveOutput,
                    cancellationToken);
                if (processResult.ExitCode != 0 || !File.Exists(temporaryPath))
                {
                    var message = FirstUsefulLine(processResult.StandardError, processResult.StandardOutput) ??
                                  $"OCRmyPDF exited with code {processResult.ExitCode}.";
                    var failed = FailedInPlace(sourcePath, originalInspection.PageCount, originalBytes, message);
                    results.Add(failed);
                    progress?.Report(new(results.Count, files.Count, fileName, "Failed", failed));
                    continue;
                }

                progress?.Report(new(
                    index,
                    files.Count,
                    fileName,
                    "Verifying output",
                    CurrentFileProgress: 0.90,
                    TotalPages: totalPages,
                    Detail: "OCRmyPDF finished. Checking the temporary PDF before replacement."));
                var outputBytes = new FileInfo(temporaryPath).Length;
                if (outputBytes <= 0)
                {
                    var failed = FailedInPlace(
                        sourcePath,
                        originalInspection.PageCount,
                        originalBytes,
                        "Verification failed: OCRmyPDF produced an empty file.");
                    results.Add(failed);
                    progress?.Report(new(results.Count, files.Count, fileName, "Failed verification", failed));
                    continue;
                }

                var outputInspection = await _textInspector.InspectAsync(temporaryPath, cancellationToken);
                if (outputInspection.PageCount != originalInspection.PageCount)
                {
                    var failed = FailedInPlace(
                        sourcePath,
                        originalInspection.PageCount,
                        originalBytes,
                        $"Verification failed: expected {originalInspection.PageCount:N0} pages but found {outputInspection.PageCount:N0}.");
                    results.Add(failed);
                    progress?.Report(new(results.Count, files.Count, fileName, "Failed verification", failed));
                    continue;
                }

                if (request.Operation == PdfInPlaceOperation.Optimize && outputBytes >= originalBytes)
                {
                    var unchanged = new PdfInPlaceFileResult(
                        fileName,
                        sourcePath,
                        PdfInPlaceFileState.Unchanged,
                        originalInspection.PageCount,
                        originalInspection.PagesWithoutText,
                        originalBytes,
                        originalBytes,
                        "No smaller verified PDF was produced; the original was kept.");
                    results.Add(unchanged);
                    progress?.Report(new(results.Count, files.Count, fileName, "Original kept", unchanged));
                    continue;
                }

                progress?.Report(new(
                    index,
                    files.Count,
                    fileName,
                    "Replacing original",
                    CurrentFileProgress: 0.97,
                    TotalPages: totalPages,
                    Detail: "Verification passed. Safely replacing the original PDF."));
                var recoveryPoint = _recoveryService.ReplaceFile(
                    temporaryPath,
                    sourcePath,
                    request.Operation == PdfInPlaceOperation.WholeFileOcr
                        ? "Whole-file OCR"
                        : "PDF Optimization",
                    _recoveryRetentionDays);

                var hasUnsearchablePages =
                    request.Operation == PdfInPlaceOperation.WholeFileOcr &&
                    outputInspection.PagesWithoutText > 0;
                var completed = new PdfInPlaceFileResult(
                    fileName,
                    sourcePath,
                    hasUnsearchablePages
                        ? PdfInPlaceFileState.ReplacedWithWarning
                        : PdfInPlaceFileState.Replaced,
                    outputInspection.PageCount,
                    outputInspection.PagesWithoutText,
                    originalBytes,
                    outputBytes,
                    hasUnsearchablePages
                        ? $"Original replaced, but {outputInspection.PagesWithoutText:N0} page(s) still have no extractable text."
                        : request.Operation == PdfInPlaceOperation.WholeFileOcr
                            ? "Original replaced with a verified whole-file OCR version."
                            : $"Original replaced with a verified smaller PDF; saved {FormatBytes(originalBytes - outputBytes)}.",
                    recoveryPoint.Id);
                results.Add(completed);
                progress?.Report(new(results.Count, files.Count, fileName, "Completed", completed));
            }
            catch (OperationCanceledException)
            {
                TryDelete(temporaryPath);
                var cancelled = new PdfInPlaceFileResult(
                    fileName,
                    sourcePath,
                    PdfInPlaceFileState.Cancelled,
                    0,
                    0,
                    originalBytes,
                    originalBytes,
                    "Processing was cancelled; the original was not changed.");
                results.Add(cancelled);
                progress?.Report(new(results.Count, files.Count, fileName, "Cancelled", cancelled));
                return new(results, WasCancelled: true);
            }
            catch (Exception exception)
            {
                var failed = FailedInPlace(sourcePath, 0, originalBytes, exception.Message);
                results.Add(failed);
                progress?.Report(new(results.Count, files.Count, fileName, "Failed", failed));
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }

        return new(results, WasCancelled: false);
    }

    private async Task<OcrProcessResult> RunOcrAsync(
        string sourcePath,
        string temporaryOutputPath,
        PdfOcrPageMode pageMode,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>(_ocrCommand!.PrefixArguments)
        {
            PageModeArgument(pageMode),
            "--output-type", "pdf",
            "--optimize", "0",
            "--jobs", "1",
            "--no-overwrite",
            sourcePath,
            temporaryOutputPath
        };
        return await _processRunner.RunAsync(
            new(_ocrCommand.Executable, arguments, Path.GetDirectoryName(temporaryOutputPath)),
            cancellationToken);
    }

    private async Task<OcrProcessResult> RunInPlaceAsync(
        PdfInPlaceRequest request,
        string sourcePath,
        string temporaryOutputPath,
        IProgress<OcrProcessOutput>? outputProgress,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>(_ocrCommand!.PrefixArguments);
        if (request.Operation == PdfInPlaceOperation.WholeFileOcr)
        {
            arguments.AddRange([
                "--force-ocr",
                "--output-type", "pdf",
                "--optimize", "0"
            ]);
        }
        else
        {
            var optimizationLevel = (int)request.OptimizationLevel;
            if (optimizationLevel is < 1 or > 3)
                throw new ArgumentOutOfRangeException(
                    nameof(request), request.OptimizationLevel, "Unsupported optimization level.");
            arguments.AddRange([
                "--ocr-engine", "none",
                "--skip-text",
                "--output-type", "pdf",
                "--optimize", optimizationLevel.ToString()
            ]);
        }

        arguments.AddRange([
            "--jobs", "1",
            "--no-overwrite",
            sourcePath,
            temporaryOutputPath
        ]);
        return await _processRunner.RunAsync(
            new(_ocrCommand.Executable, arguments, Path.GetDirectoryName(temporaryOutputPath)),
            cancellationToken,
            outputProgress);
    }

    private static string PageModeArgument(PdfOcrPageMode pageMode) => pageMode switch
    {
        PdfOcrPageMode.MissingTextOnly => "--skip-text",
        PdfOcrPageMode.AllPages => "--force-ocr",
        _ => throw new ArgumentOutOfRangeException(nameof(pageMode), pageMode, "Unsupported OCR page mode.")
    };

    private async Task<(OcrDependencyStatus Status, OcrCommand? Command)> ResolveOcrMyPdfAsync(
        CancellationToken cancellationToken)
    {
        var candidates = BuildOcrMyPdfCandidates();

        foreach (var candidate in candidates)
        {
            var versionArguments = candidate.PrefixArguments.Concat(["--version"]).ToList();
            var result = await TryRunAsync(candidate.Executable, versionArguments, cancellationToken);
            if (result is not { ExitCode: 0 }) continue;
            var version = FirstUsefulLine(result.StandardOutput, result.StandardError) ?? "Available";
            return (new(OcrMyPdfName, true, version, $"Using {candidate.Executable}."), candidate);
        }

        return (new(OcrMyPdfName, false, string.Empty,
            "Install OCRmyPDF with: python -m pip install ocrmypdf"), null);
    }

    private static IReadOnlyList<OcrCommand> BuildOcrMyPdfCandidates()
    {
        var candidates = new List<OcrCommand>
        {
            new OcrCommand("ocrmypdf", []),
            new OcrCommand("ocrmypdf.exe", []),
            new OcrCommand("py", ["-m", "ocrmypdf"]),
            new OcrCommand("py.exe", ["-m", "ocrmypdf"]),
            new OcrCommand("python", ["-m", "ocrmypdf"]),
            new OcrCommand("python.exe", ["-m", "ocrmypdf"])
        };

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var pythonRoot in new[]
                 {
                     Path.Combine(localAppData, "Programs", "Python"),
                     Path.Combine(localAppData, "Python")
                 })
        {
            foreach (var directory in TryGetDirectories(pythonRoot).OrderDescending())
            {
                var executable = Path.Combine(directory, "python.exe");
                if (File.Exists(executable)) candidates.Insert(0, new(executable, ["-m", "ocrmypdf"]));
                var script = Path.Combine(directory, "Scripts", "ocrmypdf.exe");
                if (File.Exists(script)) candidates.Insert(0, new(script, []));
            }
        }
        return candidates;
    }

    private async Task<OcrDependencyStatus> ProbeFirstAsync(
        string name,
        IReadOnlyList<string> candidates,
        IReadOnlyList<string> versionArguments,
        string missingDetails,
        CancellationToken cancellationToken,
        bool isRequired = true)
    {
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var result = await TryRunAsync(candidate, versionArguments, cancellationToken);
            if (result is not { ExitCode: 0 }) continue;
            var version = FirstUsefulLine(result.StandardOutput, result.StandardError) ?? "Available";
            return new(name, true, version, $"Using {candidate}.", isRequired);
        }
        return new(name, false, string.Empty, missingDetails, isRequired);
    }

    private async Task<OcrProcessResult?> TryRunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            return await _processRunner.RunAsync(new(executable, arguments), timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> BuildTesseractCandidates()
    {
        var candidates = new List<string> { "tesseract", "tesseract.exe" };
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
            candidates.Insert(0, Path.Combine(programFiles, "Tesseract-OCR", "tesseract.exe"));
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
            candidates.Insert(0, Path.Combine(localAppData, "Programs", "Tesseract-OCR", "tesseract.exe"));
        return candidates;
    }

    private static IReadOnlyList<string> BuildGhostscriptCandidates()
    {
        var candidates = new List<string> { "gswin64c", "gswin64c.exe" };
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new[]
        {
            string.IsNullOrWhiteSpace(programFiles) ? string.Empty : Path.Combine(programFiles, "gs"),
            string.IsNullOrWhiteSpace(localAppData) ? string.Empty : Path.Combine(localAppData, "Programs", "gs")
        };
        foreach (var root in roots)
        {
            foreach (var path in TryGetDirectories(root).OrderDescending())
                candidates.Insert(0, Path.Combine(path, "bin", "gswin64c.exe"));
        }
        return candidates;
    }

    private static IReadOnlyList<string> TryGetDirectories(string root)
    {
        try { return Directory.Exists(root) ? Directory.GetDirectories(root) : []; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return []; }
    }

    private static string BuildOutputPath(string sourceRoot, string outputRoot, ReportRow row)
    {
        var sourcePath = Path.GetFullPath(row.FullPath);
        if (!IsSameOrNestedPath(sourcePath, sourceRoot))
            throw new InvalidOperationException($"Source PDF is outside the selected archive root: {row.FileName}");

        var relativeFolder = row.RelativeFolder == "." ? string.Empty : row.RelativeFolder;
        var outputDirectory = Path.GetFullPath(Path.Combine(outputRoot, relativeFolder));
        if (!IsSameOrNestedPath(outputDirectory, outputRoot))
            throw new InvalidOperationException($"Unsafe relative output path for {row.FileName}.");
        return Path.Combine(outputDirectory, row.FileName);
    }

    private static string RequireExistingDirectory(string path, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException($"{label} not found: {fullPath}");
        return fullPath;
    }

    private static void EnsureSeparateRoots(string sourceRoot, string outputRoot)
    {
        if (IsSameOrNestedPath(outputRoot, sourceRoot) || IsSameOrNestedPath(sourceRoot, outputRoot))
            throw new InvalidOperationException("Choose an OCR output folder that is separate from the source folder.");
    }

    private static bool IsSameOrNestedPath(string candidate, string root)
    {
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase)) return true;
        return normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static PdfOcrFileResult Failed(ReportRow row, string outputPath, string message) =>
        new(row.FileName, row.FullPath, outputPath, PdfOcrFileState.Failed,
            row.PageCount ?? 0, 0, 0, message);

    private static PdfInPlaceFileResult FailedInPlace(
        string sourcePath,
        int pageCount,
        long originalBytes,
        string message) =>
        new(
            Path.GetFileName(sourcePath),
            sourcePath,
            PdfInPlaceFileState.Failed,
            pageCount,
            0,
            originalBytes,
            originalBytes,
            message);

    private static string BuildTemporaryPath(string sourcePath, string label)
    {
        var directory = Path.GetDirectoryName(sourcePath) ??
                        throw new InvalidOperationException("The PDF does not have a valid parent folder.");
        return Path.Combine(
            directory,
            $".{Path.GetFileNameWithoutExtension(sourcePath)}.{Guid.NewGuid():N}.archiveassist.{label}.pdf");
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1024 * 1024
            ? $"{bytes / (1024d * 1024d):N1} MB"
            : $"{bytes / 1024d:N1} KB";

    private static int? TryReadPageNumber(string line, int expectedPages)
    {
        if (expectedPages <= 0 || string.IsNullOrWhiteSpace(line)) return null;

        var match = LeadingPageNumberPattern.Match(line);
        if (match.Success &&
            int.TryParse(match.Groups["page"].Value, out var leadingPage) &&
            leadingPage >= 1 && leadingPage <= expectedPages)
            return leadingPage;

        match = FractionalPagePattern.Match(line);
        if (!match.Success ||
            !int.TryParse(match.Groups["page"].Value, out var fractionalPage) ||
            !int.TryParse(match.Groups["total"].Value, out var reportedTotal) ||
            reportedTotal != expectedPages ||
            fractionalPage < 1 ||
            fractionalPage > expectedPages)
            return null;
        return fractionalPage;
    }

    private static string? FriendlyOcrDetail(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var detail = AnsiEscapePattern.Replace(line, string.Empty)
            .Replace('\r', ' ')
            .Replace('�', ' ')
            .Trim();
        detail = LeadingPageNumberPattern.Replace(detail, string.Empty).Trim();
        if (detail.Length == 0 ||
            detail.Contains("[WinError 2] The system cannot find the file specified", StringComparison.OrdinalIgnoreCase))
            return null;

        if (detail.Contains("page already has text", StringComparison.OrdinalIgnoreCase))
            detail = "Rasterizing the page's existing text and running OCR.";
        if (detail.Length > 220) detail = detail[..217] + "...";
        return detail;
    }

    private static string? FirstUsefulLine(params string[] values) => values
        .SelectMany(value => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A later run uses a unique part filename, so cleanup failure cannot overwrite user data.
        }
    }

    private sealed record OcrCommand(string Executable, IReadOnlyList<string> PrefixArguments);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
