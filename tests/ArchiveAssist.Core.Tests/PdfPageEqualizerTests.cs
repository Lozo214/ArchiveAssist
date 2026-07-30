using ArchiveAssist.Core.Models;
using ArchiveAssist.Core.Services;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace ArchiveAssist.Core.Tests;

public sealed class PdfPageEqualizerTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "ArchiveAssist.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PreviewCountsOutputOnlyForFoldersThatNeedWork()
    {
        var source = Directory.CreateDirectory(Path.Combine(_testRoot, "Project")).FullName;
        var needsWork = Directory.CreateDirectory(Path.Combine(source, "A")).FullName;
        var alreadyValid = Directory.CreateDirectory(Path.Combine(source, "B")).FullName;
        WritePdf(Path.Combine(needsWork, "large.pdf"), 6);
        WritePdf(Path.Combine(alreadyValid, "small.pdf"), 3);

        var preview = await new PdfPageEqualizer().PreviewAsync(source, 5);

        Assert.Equal(2, preview.SourceFiles);
        Assert.Equal(9, preview.SourcePages);
        Assert.Equal(1, preview.FilesOverLimit);
        Assert.Equal(2, preview.ExpectedOutputFiles);
        Assert.Equal(1, preview.FoldersRequiringWork);
        Assert.Equal(0, preview.Folders.Single(folder => folder.RelativeFolder == "B").ExpectedOutputFiles);
    }

    [Fact]
    public async Task EqualizeRedistributesPagesWithoutChangingSources()
    {
        var source = Directory.CreateDirectory(Path.Combine(_testRoot, "Project")).FullName;
        var first = WritePdf(Path.Combine(source, "A.pdf"), 6);
        var second = WritePdf(Path.Combine(source, "B.pdf"), 3);
        var third = WritePdf(Path.Combine(source, "C.pdf"), 4);
        var outputRoot = Path.Combine(_testRoot, "Output", "Equalized PDFs");

        var result = await new PdfPageEqualizer().EqualizeAsync(source, outputRoot, 5);

        Assert.Equal([5, 5, 3], result.OutputPdfPaths.Select(PageCount));
        Assert.Equal([6, 3, 4], new[] { first, second, third }.Select(PageCount));
        Assert.Equal(
            [600, 601, 602, 603, 604, 605, 600, 601, 602, 600, 601, 602, 603],
            result.OutputPdfPaths.SelectMany(PageWidths));
        Assert.NotNull(result.ManifestPath);
        Assert.True(File.Exists(result.ManifestPath));
        Assert.Empty(Directory.GetFiles(outputRoot, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task EqualizeCreatesNothingWhenAllFilesAreWithinLimit()
    {
        var source = Directory.CreateDirectory(Path.Combine(_testRoot, "Project")).FullName;
        WritePdf(Path.Combine(source, "A.pdf"), 3);
        WritePdf(Path.Combine(source, "B.pdf"), 5);
        var outputRoot = Path.Combine(_testRoot, "Equalized PDFs");

        var result = await new PdfPageEqualizer().EqualizeAsync(source, outputRoot, 5);

        Assert.Empty(result.OutputPdfPaths);
        Assert.Null(result.ManifestPath);
        Assert.False(Directory.Exists(outputRoot));
    }

    [Fact]
    public async Task EqualizePreservesSubfoldersAndDoesNotMixPages()
    {
        var source = Directory.CreateDirectory(Path.Combine(_testRoot, "Project")).FullName;
        var firstClient = Directory.CreateDirectory(Path.Combine(source, "Client A")).FullName;
        var secondClient = Directory.CreateDirectory(Path.Combine(source, "Client B", "Mail")).FullName;
        WritePdf(Path.Combine(firstClient, "A.pdf"), 6);
        WritePdf(Path.Combine(firstClient, "B.pdf"), 2);
        WritePdf(Path.Combine(secondClient, "A.pdf"), 7);
        var outputRoot = Path.Combine(_testRoot, "Equalized PDFs");

        var result = await new PdfPageEqualizer().EqualizeAsync(source, outputRoot, 5);

        Assert.Equal(
            [
                Path.Combine("Client A", "Equalized_001.pdf"),
                Path.Combine("Client A", "Equalized_002.pdf"),
                Path.Combine("Client B", "Mail", "Equalized_001.pdf"),
                Path.Combine("Client B", "Mail", "Equalized_002.pdf")
            ],
            result.OutputPdfPaths.Select(path => Path.GetRelativePath(outputRoot, path)));
        Assert.Equal([5, 3, 5, 2], result.OutputPdfPaths.Select(PageCount));

        var manifestLines = await File.ReadAllLinesAsync(result.ManifestPath!);
        Assert.Equal(16, manifestLines.Length);
        Assert.Equal("Output PDF,Source PDF,Source Page", manifestLines[0]);
        Assert.DoesNotContain(manifestLines.Skip(1), line =>
            line.Contains("Client A", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("Client B", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PreviewIgnoresPreviouslyGeneratedEqualizedFolder()
    {
        var source = Directory.CreateDirectory(Path.Combine(_testRoot, "Project")).FullName;
        var generated = Directory.CreateDirectory(Path.Combine(source, "Equalized PDFs")).FullName;
        WritePdf(Path.Combine(source, "Source.pdf"), 2);
        WritePdf(Path.Combine(generated, "Generated.pdf"), 9);

        var preview = await new PdfPageEqualizer().PreviewAsync(source, 5);

        Assert.Equal(1, preview.SourceFiles);
        Assert.Equal(2, preview.SourcePages);
        Assert.False(preview.HasWork);
    }

    [Fact]
    public async Task CancellationRemovesAllPartialOutput()
    {
        var source = Directory.CreateDirectory(Path.Combine(_testRoot, "Project")).FullName;
        WritePdf(Path.Combine(source, "Large.pdf"), 12);
        var outputRoot = Path.Combine(_testRoot, "Equalized PDFs");
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<EqualizationProgress>(update =>
        {
            if (update.Stage == "Equalizing") cancellation.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PdfPageEqualizer().EqualizeAsync(source, outputRoot, 5, progress, cancellation.Token));

        Assert.False(Directory.Exists(outputRoot));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot)) Directory.Delete(_testRoot, recursive: true);
    }

    private static string WritePdf(string path, int pageCount)
    {
        using var document = new PdfDocument();
        for (var pageNumber = 0; pageNumber < pageCount; pageNumber++)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(600 + pageNumber);
            page.Height = XUnit.FromPoint(800);
        }
        document.Save(path);
        return path;
    }

    private static int PageCount(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return document.PageCount;
    }

    private static IEnumerable<int> PageWidths(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return document.Pages.Cast<PdfPage>().Select(page => (int)Math.Round(page.Width.Point)).ToList();
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
