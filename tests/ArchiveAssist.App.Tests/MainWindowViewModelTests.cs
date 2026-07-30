using System.IO;
using ArchiveAssist.App.Models;
using ArchiveAssist.App.Services;
using ArchiveAssist.App.ViewModels;
using ArchiveAssist.Core.Models;
using ArchiveAssist.Core.Services;

namespace ArchiveAssist.App.Tests;

public sealed class MainWindowViewModelTests : IDisposable
{
    private readonly string _testRoot = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(),
        "ArchiveAssist.MainViewModel.Tests",
        Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public async Task ReplaceSelectionBuildsFriendlySummaryAndRemembersLocation()
    {
        var folder = Directory.CreateDirectory(
            Path.Combine(_testRoot, "Archive")).FullName;
        var pdf = Path.Combine(folder, "one.pdf");
        File.WriteAllText(pdf, "test");
        var settings = new AppSettings();
        var viewModel = CreateViewModel(settings);
        var selectionChangedCount = 0;
        viewModel.SelectionChanged += (_, _) => selectionChangedCount++;

        await viewModel.ReplaceSelectionAsync([folder]);

        Assert.True(viewModel.HasSelection);
        Assert.True(viewModel.HasDiscoveredFiles);
        Assert.Equal("1 folder", viewModel.SelectionSummary);
        Assert.Equal("Archive", viewModel.CountContextLabel);
        Assert.Equal(1, selectionChangedCount);
        Assert.Equal("Discovery (1)", viewModel.DiscoveryTabHeader);
        Assert.Contains(folder, settings.RecentScanPaths);
    }

    [Fact]
    public void SearchAndCategoryFilterCombineAndClipboardHeadersAreOptional()
    {
        var settings = new AppSettings { IncludeClipboardHeaders = false };
        var viewModel = CreateViewModel(settings);
        var warningRow = CreateRow(
            "Map warning.pdf",
            ArchiveFileKind.Pdf,
            maps: 1,
            warning: "Page count exceeds limit");
        var photoRow = CreateRow("Portrait.jpg", ArchiveFileKind.Photo, photos: 1);
        viewModel.Results.Add(warningRow);
        viewModel.Results.Add(photoRow);

        viewModel.ReportSearchText = "map";
        viewModel.ApplyReportFilter("Files with warnings");

        Assert.Equal([warningRow], viewModel.VisibleReportRows());
        var clipboard = viewModel.BuildClipboardText([warningRow]);
        Assert.DoesNotContain("File Name\tDocuments", clipboard);
        Assert.StartsWith("Map warning.pdf", clipboard);
    }

    private MainWindowViewModel CreateViewModel(AppSettings settings) =>
        new(
            new NullPathPicker(),
            new FakeScanner(_testRoot),
            new MemorySettingsService(),
            settings);

    private ReportRow CreateRow(
        string name,
        ArchiveFileKind kind,
        int maps = 0,
        int photos = 0,
        string? warning = null) =>
        new(
            name,
            ".",
            Path.Combine(_testRoot, name),
            kind,
            100,
            kind == ArchiveFileKind.Pdf ? 1 : null,
            kind == ArchiveFileKind.Pdf ? 1 - maps : 0,
            maps,
            photos,
            0,
            null,
            false,
            warning,
            null);

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private sealed class NullPathPicker : IScanPathPicker
    {
        public IReadOnlyList<string>? PickPaths(
            IReadOnlyList<string> currentPaths,
            string? initialDirectory = null) =>
            null;
    }

    private sealed class MemorySettingsService : ISettingsService
    {
        public AppSettings Load() => new();

        public void Save(AppSettings settings)
        {
        }
    }

    private sealed class FakeScanner(string root) : IPdfFolderScanner
    {
        public Task<PdfDiscoveryResult> DiscoverAsync(
            string folderPath,
            CancellationToken cancellationToken = default) =>
            DiscoverAsync([folderPath], cancellationToken);

        public Task<PdfDiscoveryResult> DiscoverAsync(
            IReadOnlyList<string> paths,
            CancellationToken cancellationToken = default)
        {
            var files = paths
                .SelectMany(path => Directory.Exists(path)
                    ? Directory.GetFiles(path)
                    : [path])
                .Select(path => new DiscoveredFile(
                    path,
                    Path.GetFileName(path),
                    ".",
                    Path.GetExtension(path),
                    ArchiveFileKind.Pdf))
                .ToList();
            return Task.FromResult(new PdfDiscoveryResult(root, files, [], paths));
        }

        public Task<PdfScanOutcome> ScanAsync(
            PdfDiscoveryResult discovery,
            ScanOptions options,
            IProgress<PdfScanProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
