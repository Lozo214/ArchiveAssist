using ArchiveAssist.Core.Models;
using ArchiveAssist.Core.Services;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using PdfPigPoint = UglyToad.PdfPig.Core.PdfPoint;
using PdfPigPageSize = UglyToad.PdfPig.Content.PageSize;

namespace ArchiveAssist.Core.Tests;

public sealed class PdfFolderScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ArchiveAssist-{Guid.NewGuid():N}");
    private readonly PdfFolderScanner _scanner = new();

    public PdfFolderScannerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void DefaultPageSizePresetUsesStandardTwelveByEighteenScannerRule()
    {
        Assert.Equal(PageSizePreset.StandardScannerName, PageSizePreset.Default.Name);
        Assert.Equal(12, PageSizePreset.Default.Width);
        Assert.Equal(18, PageSizePreset.Default.Height);
        Assert.True(PageSizePreset.Default.UseFeederRule);
    }

    [Fact]
    public async Task DiscoverAsync_FindsNestedPdfsAndIgnoresGeneratedOutput()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_root, "Box 1")).FullName;
        var generated = Directory.CreateDirectory(Path.Combine(_root, "Equalized PDFs")).FullName;
        WritePdf(Path.Combine(_root, "First.pdf"), (8.5, 11));
        WritePdf(Path.Combine(nested, "Second.PDF"), (8.5, 11));
        WritePdf(Path.Combine(generated, "Generated.pdf"), (8.5, 11));

        var discovery = await _scanner.DiscoverAsync(_root);

        Assert.Equal(2, discovery.Files.Count);
        Assert.Contains(discovery.Files, file => file.RelativeFolder == "Box 1");
        Assert.DoesNotContain(discovery.Files, file => file.FileName == "Generated.pdf");
    }

    [Fact]
    public async Task DiscoverAsync_AcceptsMixedFilesAndFoldersAndRemovesOverlaps()
    {
        var selectedFolder = Directory.CreateDirectory(Path.Combine(_root, "Selected folder")).FullName;
        var nestedPdf = Path.Combine(selectedFolder, "Nested.pdf");
        var individualPdf = Path.Combine(_root, "Individual.pdf");
        var unselectedPdf = Path.Combine(_root, "Not selected.pdf");
        WritePdf(nestedPdf, (8.5, 11));
        WritePdf(individualPdf, (8.5, 11));
        WritePdf(unselectedPdf, (8.5, 11));

        var discovery = await _scanner.DiscoverAsync([selectedFolder, nestedPdf, individualPdf]);

        Assert.Equal(2, discovery.Files.Count);
        Assert.Contains(discovery.Files, file => file.FullPath == nestedPdf);
        Assert.Contains(discovery.Files, file => file.FullPath == individualPdf);
        Assert.DoesNotContain(discovery.Files, file => file.FullPath == unselectedPdf);
        Assert.Equal(2, discovery.SelectedPaths.Count);
    }

    [Fact]
    public async Task ScanAsync_CountsDocumentsAndMapsUsingVisiblePageSizes()
    {
        WritePdf(Path.Combine(_root, "Mixed.pdf"), (8.5, 11), (11, 17));
        var discovery = await _scanner.DiscoverAsync(_root);
        var letter = PageSizePreset.BuiltIn.Single(preset => preset.Name == "Letter");

        var outcome = await _scanner.ScanAsync(discovery, new ScanOptions(letter));

        var result = Assert.Single(outcome.Results);
        Assert.Equal(2, result.PageCount);
        Assert.Equal(1, result.Documents);
        Assert.Equal(1, result.Maps);
        Assert.Equal(1, outcome.Summary.Documents);
        Assert.Equal(1, outcome.Summary.Maps);
    }

    [Fact]
    public async Task ScanAsync_StandardScannerAllowsLongPagesThroughTwelveInchFeeder()
    {
        WritePdf(Path.Combine(_root, "Long.pdf"), (11, 30), (13, 14));
        var discovery = await _scanner.DiscoverAsync(_root);
        var standard = PageSizePreset.BuiltIn.Single(preset => preset.UseFeederRule);

        var outcome = await _scanner.ScanAsync(discovery, new ScanOptions(standard));

        Assert.Equal(1, outcome.Summary.Documents);
        Assert.Equal(1, outcome.Summary.Maps);
    }

    [Fact]
    public async Task ScanAsync_ContinuesAfterUnreadablePdfAndReportsProgress()
    {
        File.WriteAllText(Path.Combine(_root, "Broken.pdf"), "not a PDF");
        WritePdf(Path.Combine(_root, "Good.pdf"), (8.5, 11));
        var discovery = await _scanner.DiscoverAsync(_root);
        var updates = new List<PdfScanProgress>();

        var outcome = await _scanner.ScanAsync(
            discovery, new ScanOptions(PageSizePreset.Default), new ImmediateProgress<PdfScanProgress>(updates.Add));

        Assert.Equal(2, outcome.Results.Count);
        Assert.Equal(1, outcome.Summary.ErrorCount);
        Assert.Equal(2, updates.Count);
        Assert.Contains(outcome.Results, result => !result.IsSuccessful && result.Error!.StartsWith("Could not open PDF:"));
    }

    [Fact]
    public async Task ScanAsync_ReturnsPartialOutcomeWhenCancelled()
    {
        WritePdf(Path.Combine(_root, "One.pdf"), (8.5, 11));
        var discovery = await _scanner.DiscoverAsync(_root);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var outcome = await _scanner.ScanAsync(
            discovery, new ScanOptions(PageSizePreset.Default), cancellationToken: cancellation.Token);

        Assert.True(outcome.WasCancelled);
        Assert.Empty(outcome.Results);
    }

    [Fact]
    public async Task ScanAsync_CountsPhotosPhotoBacksAndSkippedFiles()
    {
        File.WriteAllText(Path.Combine(_root, "Front.JPG"), "photo");
        File.WriteAllText(Path.Combine(_root, "Front_BACK.tif"), "photo back");
        File.WriteAllText(Path.Combine(_root, "Reverse_b.png"), "short photo back suffix");
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "unsupported");
        var discovery = await _scanner.DiscoverAsync(_root);

        var outcome = await _scanner.ScanAsync(discovery, new ScanOptions(PageSizePreset.Default));

        Assert.Equal(1, discovery.PhotoCount);
        Assert.Equal(2, discovery.PhotoBackCount);
        Assert.Equal(1, discovery.SkippedCount);
        Assert.Equal(2, outcome.Summary.Documents);
        Assert.Equal(1, outcome.Summary.Photos);
        Assert.Equal(2, outcome.Summary.PhotoBacks);
        Assert.Equal(3, outcome.Summary.Total);
        Assert.Equal(1, outcome.Summary.SkippedFiles);
        Assert.Contains(outcome.Results, row => row.Kind == ArchiveFileKind.PhotoBack && row.Total == 1);
    }

    [Fact]
    public async Task ScanAsync_DoesNotTrackCroppedOrMixedSizesAsIssues()
    {
        using (var document = new PdfDocument())
        {
            var cropped = document.AddPage();
            cropped.Width = XUnit.FromInch(11);
            cropped.Height = XUnit.FromInch(17);
            cropped.CropBox = new PdfRectangle(
                new XPoint(0, 0),
                new XSize(8.5 * 72, 11 * 72));

            var legal = document.AddPage();
            legal.Width = XUnit.FromInch(8.5);
            legal.Height = XUnit.FromInch(14);
            document.Save(Path.Combine(_root, "Warnings.pdf"));
        }
        var discovery = await _scanner.DiscoverAsync(_root);

        var outcome = await _scanner.ScanAsync(
            discovery, new ScanOptions(PageSizePreset.Default, MaxPagesPerPdf: 1));

        var result = Assert.Single(outcome.Results);
        Assert.True(result.OverPageLimit);
        Assert.True(result.HasWarning);
        Assert.DoesNotContain("cropped", result.IssuesLabel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mixed", result.IssuesLabel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Page count exceeds limit", result.IssuesLabel);
        Assert.Equal(1, outcome.Summary.FilesOverPageLimit);
    }

    [Fact]
    public async Task ScanAsync_FastCountSkipsSearchableTextChecks()
    {
        WritePdf(Path.Combine(_root, "Blank.pdf"), (8.5, 11));
        var discovery = await _scanner.DiscoverAsync(_root);

        var outcome = await _scanner.ScanAsync(
            discovery,
            new ScanOptions(PageSizePreset.Default, QaMode: PdfQaMode.FastCountOnly));

        var result = Assert.Single(outcome.Results);
        Assert.Null(result.SearchableText);
        Assert.Equal(0, result.OcrPagesChecked);
        Assert.False(result.OcrCheckComplete);
        Assert.Equal("Skipped", result.OcrCheckStatusLabel);
        Assert.Equal("Not checked", result.SearchableTextLabel);
        Assert.Empty(result.NonOcrPageNumbers);
        Assert.False(result.HasWarning);
    }

    [Fact]
    public async Task ScanAsync_StandardQaSamplesPagesAndStopsAfterFindingText()
    {
        WriteTextPdf(
            Path.Combine(_root, "Sampled.pdf"),
            null, null, null, null, null, null, null, null, null, "Searchable last page");
        var discovery = await _scanner.DiscoverAsync(_root);

        var outcome = await _scanner.ScanAsync(
            discovery,
            new ScanOptions(PageSizePreset.Default, QaMode: PdfQaMode.StandardQa));

        var result = Assert.Single(outcome.Results);
        Assert.True(result.SearchableText);
        Assert.Equal(8, result.OcrPagesChecked);
        Assert.False(result.OcrCheckComplete);
        Assert.Equal(1, result.PagesWithText);
        Assert.Equal(0, result.PagesWithoutText);
        Assert.Empty(result.NonOcrPageNumbers);
        Assert.Equal("Likely yes", result.SearchableTextLabel);
        Assert.Equal(0, outcome.Summary.NonSearchablePdfs);
    }

    [Fact]
    public async Task ScanAsync_StandardQaWarnsWithoutClaimingExactNonOcrPages()
    {
        WritePdf(Path.Combine(_root, "Blank.pdf"), (8.5, 11), (8.5, 11));
        var discovery = await _scanner.DiscoverAsync(_root);

        var outcome = await _scanner.ScanAsync(
            discovery,
            new ScanOptions(PageSizePreset.Default, QaMode: PdfQaMode.StandardQa));

        var result = Assert.Single(outcome.Results);
        Assert.False(result.SearchableText);
        Assert.Equal(2, result.OcrPagesChecked);
        Assert.Equal(0, result.PagesWithoutText);
        Assert.Empty(result.NonOcrPageNumbers);
        Assert.Equal("Likely no", result.SearchableTextLabel);
        Assert.Contains("No searchable text found in sampled pages", result.Warning);
        Assert.Equal(1, outcome.Summary.NonSearchablePdfs);
        Assert.Equal(0, outcome.Summary.FilesWithNonOcrPages);
    }

    [Fact]
    public async Task ScanAsync_DeepOcrCheckReportsExactPagesWithoutText()
    {
        WriteTextPdf(Path.Combine(_root, "MixedText.pdf"), "First", null, "Third");
        var discovery = await _scanner.DiscoverAsync(_root);

        var outcome = await _scanner.ScanAsync(
            discovery,
            new ScanOptions(PageSizePreset.Default, QaMode: PdfQaMode.DeepOcrCheck));

        var result = Assert.Single(outcome.Results);
        Assert.False(result.SearchableText);
        Assert.True(result.OcrCheckComplete);
        Assert.Equal(3, result.OcrPagesChecked);
        Assert.Equal(2, result.PagesWithText);
        Assert.Equal(1, result.PagesWithoutText);
        Assert.Equal([2], result.NonOcrPageNumbers);
        Assert.Equal("2", result.NonOcrPageNumbersLabel);
        Assert.Equal("No", result.SearchableTextLabel);
        Assert.Contains("Pages without searchable text found", result.Warning);
        Assert.Equal(2, outcome.Summary.PagesWithText);
        Assert.Equal(1, outcome.Summary.PagesWithoutText);
        Assert.Equal(1, outcome.Summary.FilesWithNonOcrPages);
        Assert.Equal(1, outcome.Summary.NonSearchablePdfs);
    }

    [Fact]
    public void ReportRow_ToStringDoesNotRecurseThroughPageSize()
    {
        var result = new ReportRow(
            "Sample.pdf", ".", "C:\\Sample.pdf", ArchiveFileKind.Pdf, 1024, 1,
            1, 0, 0, 0, new PageSize(8.5, 11), false, null, null);

        var displayText = result.ToString();

        Assert.Equal("PDF: Sample.pdf", displayText);
        Assert.Equal("8.50 x 11.00 in", result.LargestPageSizeLabel);
    }

    private static void WritePdf(string path, params (double Width, double Height)[] pageSizes)
    {
        using var document = new PdfDocument();
        foreach (var (width, height) in pageSizes)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromInch(width);
            page.Height = XUnit.FromInch(height);
        }

        document.Save(path);
    }

    private static void WriteTextPdf(string path, params string?[] pageTexts)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pageTexts)
        {
            var page = builder.AddPage(PdfPigPageSize.Letter);
            if (!string.IsNullOrWhiteSpace(text))
            {
                page.AddText(text, 12, new PdfPigPoint(72, 700), font);
            }
        }

        File.WriteAllBytes(path, builder.Build());
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
