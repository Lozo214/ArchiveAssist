using System.Security.Cryptography;
using ArchiveAssist.Core.Models;
using ArchiveAssist.Core.Services;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using PdfPigPoint = UglyToad.PdfPig.Core.PdfPoint;
using PdfPigPageSize = UglyToad.PdfPig.Content.PageSize;

namespace ArchiveAssist.Core.Tests;

public sealed class OcrMyPdfServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ArchiveAssist.Ocr.Tests", Guid.NewGuid().ToString("N"));

    public OcrMyPdfServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task SetupCheckReportsAllRequiredComponents()
    {
        var runner = new FakeOcrProcessRunner(SearchablePdfBytes());
        var service = new OcrMyPdfService(runner);

        var status = await service.CheckAvailabilityAsync();

        Assert.True(status.IsReady);
        Assert.Equal(["OCRmyPDF", "Tesseract", "Ghostscript"], status.Dependencies.Select(item => item.Name));
        Assert.All(status.Dependencies, item => Assert.True(item.IsAvailable));
    }

    [Fact]
    public async Task SetupIsReadyWhenOnlyOptionalGhostscriptIsMissing()
    {
        var runner = new FakeOcrProcessRunner(SearchablePdfBytes()) { MissingGhostscript = true };
        var service = new OcrMyPdfService(runner);

        var status = await service.CheckAvailabilityAsync();

        Assert.True(status.IsReady);
        var ghostscript = Assert.Single(status.Dependencies, item => item.Name == "Ghostscript");
        Assert.False(ghostscript.IsAvailable);
        Assert.False(ghostscript.IsRequired);
        Assert.Equal("Optional", ghostscript.StatusLabel);
    }

    [Fact]
    public async Task OptimizationSetupDoesNotRequireTesseract()
    {
        var runner = new FakeOcrProcessRunner(BlankPdfBytes()) { MissingTesseract = true };
        var service = new OcrMyPdfService(runner);

        var status = await service.CheckInPlaceAvailabilityAsync(PdfInPlaceOperation.Optimize);

        Assert.True(status.IsReady);
        var dependency = Assert.Single(status.Dependencies);
        Assert.Equal("OCRmyPDF", dependency.Name);
    }

    [Fact]
    public async Task CreatesVerifiedCopyInMatchingSubfolderWithoutChangingSource()
    {
        var sourceRoot = Directory.CreateDirectory(Path.Combine(_root, "Source")).FullName;
        var clientFolder = Directory.CreateDirectory(Path.Combine(sourceRoot, "Client A")).FullName;
        var outputRoot = Directory.CreateDirectory(Path.Combine(_root, "OCR Output")).FullName;
        var sourcePath = Path.Combine(clientFolder, "Scan.pdf");
        File.WriteAllBytes(sourcePath, BlankPdfBytes());
        var originalHash = Hash(sourcePath);
        var runner = new FakeOcrProcessRunner(SearchablePdfBytes());
        var service = new OcrMyPdfService(runner);

        var result = await service.CreateSearchableCopiesAsync(
            new(sourceRoot, outputRoot, [OcrRow(sourcePath, "Client A")]));

        var fileResult = Assert.Single(result.Results);
        Assert.Equal(PdfOcrFileState.Completed, fileResult.State);
        Assert.Equal(Path.Combine(outputRoot, "Client A", "Scan.pdf"), fileResult.OutputPath);
        Assert.True(File.Exists(fileResult.OutputPath));
        Assert.Equal(1, fileResult.PagesWithText);
        Assert.Equal(originalHash, Hash(sourcePath));
        Assert.Equal(1, runner.OcrCalls);
        Assert.Contains("--skip-text", runner.LastOcrArguments);
        Assert.DoesNotContain("--force-ocr", runner.LastOcrArguments);
        Assert.Empty(Directory.GetFiles(outputRoot, "*.part.pdf", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AllPagesModeUsesForceOcrInsteadOfSkippingTextPages()
    {
        var sourceRoot = Directory.CreateDirectory(Path.Combine(_root, "Source")).FullName;
        var outputRoot = Directory.CreateDirectory(Path.Combine(_root, "Output")).FullName;
        var sourcePath = Path.Combine(sourceRoot, "Scan.pdf");
        File.WriteAllBytes(sourcePath, BlankPdfBytes());
        var runner = new FakeOcrProcessRunner(SearchablePdfBytes());
        var service = new OcrMyPdfService(runner);

        var result = await service.CreateSearchableCopiesAsync(
            new(sourceRoot, outputRoot, [OcrRow(sourcePath, ".")], PdfOcrPageMode.AllPages));

        Assert.Equal(PdfOcrFileState.Completed, Assert.Single(result.Results).State);
        Assert.Contains("--force-ocr", runner.LastOcrArguments);
        Assert.DoesNotContain("--skip-text", runner.LastOcrArguments);
    }

    [Fact]
    public async Task ExistingOutputIsSkippedAndNeverOverwritten()
    {
        var sourceRoot = Directory.CreateDirectory(Path.Combine(_root, "Source")).FullName;
        var outputRoot = Directory.CreateDirectory(Path.Combine(_root, "Output")).FullName;
        var sourcePath = Path.Combine(sourceRoot, "Scan.pdf");
        var outputPath = Path.Combine(outputRoot, "Scan.pdf");
        File.WriteAllBytes(sourcePath, BlankPdfBytes());
        await File.WriteAllTextAsync(outputPath, "keep me");
        var runner = new FakeOcrProcessRunner(SearchablePdfBytes());
        var service = new OcrMyPdfService(runner);

        var result = await service.CreateSearchableCopiesAsync(
            new(sourceRoot, outputRoot, [OcrRow(sourcePath, ".")]));

        Assert.Equal(PdfOcrFileState.Skipped, Assert.Single(result.Results).State);
        Assert.Equal("keep me", await File.ReadAllTextAsync(outputPath));
        Assert.Equal(0, runner.OcrCalls);
    }

    [Fact]
    public async Task FailedOcrRemovesPartialOutput()
    {
        var sourceRoot = Directory.CreateDirectory(Path.Combine(_root, "Source")).FullName;
        var outputRoot = Directory.CreateDirectory(Path.Combine(_root, "Output")).FullName;
        var sourcePath = Path.Combine(sourceRoot, "Scan.pdf");
        File.WriteAllBytes(sourcePath, BlankPdfBytes());
        var runner = new FakeOcrProcessRunner(SearchablePdfBytes()) { FailOcr = true };
        var service = new OcrMyPdfService(runner);

        var result = await service.CreateSearchableCopiesAsync(
            new(sourceRoot, outputRoot, [OcrRow(sourcePath, ".")]));

        Assert.Equal(PdfOcrFileState.Failed, Assert.Single(result.Results).State);
        Assert.Empty(Directory.GetFiles(outputRoot, "*.pdf", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task RejectsOutputFolderInsideSourceArchive()
    {
        var sourceRoot = Directory.CreateDirectory(Path.Combine(_root, "Source")).FullName;
        var outputRoot = Directory.CreateDirectory(Path.Combine(sourceRoot, "OCR Output")).FullName;
        var sourcePath = Path.Combine(sourceRoot, "Scan.pdf");
        File.WriteAllBytes(sourcePath, BlankPdfBytes());
        var service = new OcrMyPdfService(new FakeOcrProcessRunner(SearchablePdfBytes()));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateSearchableCopiesAsync(new(sourceRoot, outputRoot, [OcrRow(sourcePath, ".")])));

        Assert.Contains("separate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WholeFileOcrVerifiesAndReplacesOriginal()
    {
        var sourcePath = Path.Combine(_root, "Whole File.pdf");
        File.WriteAllBytes(sourcePath, BlankPdfBytes());
        var searchableBytes = SearchablePdfBytes();
        var runner = new FakeOcrProcessRunner(searchableBytes);
        var recoveryService = CreateRecoveryService();
        var service = new OcrMyPdfService(runner, recoveryService: recoveryService);

        var result = await service.ProcessInPlaceAsync(
            new([sourcePath], PdfInPlaceOperation.WholeFileOcr));

        var fileResult = Assert.Single(result.Results);
        Assert.Equal(PdfInPlaceFileState.Replaced, fileResult.State);
        Assert.Equal(Hash(searchableBytes), Hash(sourcePath));
        Assert.NotNull(fileResult.RecoveryPointId);
        Assert.Single(recoveryService.GetRecoveryPoints());
        Assert.Contains("--force-ocr", runner.LastOcrArguments);
        Assert.Contains("0", runner.LastOcrArguments);
        Assert.DoesNotContain("--ocr-engine", runner.LastOcrArguments);
        Assert.Empty(Directory.GetFiles(_root, "*.archiveassist.*.pdf"));
    }

    [Fact]
    public async Task WholeFileOcrReportsLivePageProgressFromEngineOutput()
    {
        var sourcePath = Path.Combine(_root, "Progress.pdf");
        File.WriteAllBytes(sourcePath, BlankPdfBytes(pageCount: 3));
        var runner = new FakeOcrProcessRunner(SearchablePdfBytes(pageCount: 3))
        {
            OcrOutputLines =
            [
                "    1 rasterizing page",
                "    2 running tesseract",
                "    3 page already has text! - rasterizing text and running OCR anyway"
            ]
        };
        var service = new OcrMyPdfService(
            runner,
            recoveryService: CreateRecoveryService());
        var updates = new List<PdfInPlaceProgress>();

        await service.ProcessInPlaceAsync(
            new([sourcePath], PdfInPlaceOperation.WholeFileOcr),
            new InlineProgress<PdfInPlaceProgress>(updates.Add));

        Assert.Contains(updates, update =>
            update.CurrentPage == 2 &&
            update.TotalPages == 3 &&
            update.Stage == "OCRing page 2 of 3" &&
            update.CurrentFileProgress is > 0 and < 0.85);
        Assert.Contains(updates, update =>
            update.CurrentPage == 3 &&
            update.Detail == "Rasterizing the page's existing text and running OCR.");
        Assert.Contains(updates, update =>
            update.Stage == "Verifying output" && update.CurrentFileProgress == 0.90);
    }

    [Fact]
    public async Task OptimizationUsesNoOcrAndKeepsOriginalWhenOutputIsNotSmaller()
    {
        var sourcePath = Path.Combine(_root, "Already Small.pdf");
        File.WriteAllBytes(sourcePath, BlankPdfBytes());
        var originalHash = Hash(sourcePath);
        var runner = new FakeOcrProcessRunner(SearchablePdfBytes());
        var service = new OcrMyPdfService(runner);

        var result = await service.ProcessInPlaceAsync(
            new([sourcePath], PdfInPlaceOperation.Optimize, PdfOptimizationLevel.Lossless));

        var fileResult = Assert.Single(result.Results);
        Assert.Equal(PdfInPlaceFileState.Unchanged, fileResult.State);
        Assert.Equal(originalHash, Hash(sourcePath));
        Assert.Contains("--ocr-engine", runner.LastOcrArguments);
        Assert.Contains("none", runner.LastOcrArguments);
        Assert.Contains("--skip-text", runner.LastOcrArguments);
        var optimizeIndex = runner.LastOcrArguments.ToList().IndexOf("--optimize");
        Assert.True(optimizeIndex >= 0);
        Assert.Equal("1", runner.LastOcrArguments[optimizeIndex + 1]);
        Assert.Empty(Directory.GetFiles(_root, "*.archiveassist.*.pdf"));
    }

    [Fact]
    public async Task OptimizationReplacesOriginalWhenVerifiedOutputIsSmaller()
    {
        var sourcePath = Path.Combine(_root, "Large.pdf");
        var paddedSource = BlankPdfBytes().Concat(new byte[16_384]).ToArray();
        File.WriteAllBytes(sourcePath, paddedSource);
        var optimizedBytes = BlankPdfBytes();
        var runner = new FakeOcrProcessRunner(optimizedBytes);
        var recoveryService = CreateRecoveryService();
        var service = new OcrMyPdfService(runner, recoveryService: recoveryService);

        var result = await service.ProcessInPlaceAsync(
            new([sourcePath], PdfInPlaceOperation.Optimize, PdfOptimizationLevel.Balanced));

        var fileResult = Assert.Single(result.Results);
        Assert.Equal(PdfInPlaceFileState.Replaced, fileResult.State);
        Assert.True(fileResult.BytesSaved > 0);
        Assert.Equal(Hash(optimizedBytes), Hash(sourcePath));
        Assert.NotNull(fileResult.RecoveryPointId);
        Assert.Single(recoveryService.GetRecoveryPoints());
        var optimizeIndex = runner.LastOcrArguments.ToList().IndexOf("--optimize");
        Assert.Equal("2", runner.LastOcrArguments[optimizeIndex + 1]);
        Assert.Empty(Directory.GetFiles(_root, "*.archiveassist.*.pdf"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static ReportRow OcrRow(string path, string relativeFolder) => new(
        Path.GetFileName(path), relativeFolder, path, ArchiveFileKind.Pdf, new FileInfo(path).Length,
        1, 1, 0, 0, 0, new PageSize(8.5, 11), false,
        "Pages without searchable text found", null, PdfQaMode.DeepOcrCheck,
        1, true, false, 0, 1, [1]);

    private FileRecoveryService CreateRecoveryService() =>
        new(Path.Combine(_root, "Managed Recovery"));

    private static byte[] BlankPdfBytes(int pageCount = 1)
    {
        var builder = new PdfDocumentBuilder();
        for (var page = 0; page < pageCount; page++)
            builder.AddPage(PdfPigPageSize.Letter);
        return builder.Build();
    }

    private static byte[] SearchablePdfBytes(int pageCount = 1)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        for (var pageNumber = 0; pageNumber < pageCount; pageNumber++)
        {
            var page = builder.AddPage(PdfPigPageSize.Letter);
            page.AddText("OCR text layer", 12, new PdfPigPoint(72, 700), font);
        }
        return builder.Build();
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private sealed class FakeOcrProcessRunner(byte[] searchableOutput) : IOcrProcessRunner
    {
        private int _ocrCalls;
        public int OcrCalls => _ocrCalls;
        public IReadOnlyList<string> LastOcrArguments { get; private set; } = [];
        public bool FailOcr { get; init; }
        public bool MissingGhostscript { get; init; }
        public bool MissingTesseract { get; init; }
        public IReadOnlyList<string> OcrOutputLines { get; init; } = [];

        public Task<OcrProcessResult> RunAsync(
            OcrProcessRequest request,
            CancellationToken cancellationToken = default,
            IProgress<OcrProcessOutput>? outputProgress = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (MissingGhostscript && request.FileName.Contains("gswin64c", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new OcrProcessResult(1, string.Empty, "not installed"));
            if (MissingTesseract && request.FileName.Contains("tesseract", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new OcrProcessResult(1, string.Empty, "not installed"));
            if (!request.Arguments.Contains("--skip-text") && !request.Arguments.Contains("--force-ocr"))
                return Task.FromResult(new OcrProcessResult(0, "17.8.1", string.Empty));

            Interlocked.Increment(ref _ocrCalls);
            LastOcrArguments = request.Arguments.ToArray();
            foreach (var line in OcrOutputLines)
                outputProgress?.Report(new(line, IsStandardError: true));
            var outputPath = request.Arguments[^1];
            File.WriteAllBytes(outputPath, searchableOutput);
            return Task.FromResult(FailOcr
                ? new OcrProcessResult(6, string.Empty, "Synthetic OCR failure")
                : new OcrProcessResult(0, "OCR complete", string.Empty));
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
