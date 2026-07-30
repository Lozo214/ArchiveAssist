using System.IO;
using ArchiveAssist.App.Models;
using ArchiveAssist.App.Services;

namespace ArchiveAssist.App.Tests;

public sealed class RecentScanLocationServiceTests : IDisposable
{
    private readonly string _testRoot = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(),
        "ArchiveAssist.RecentScan.Tests",
        Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public void AddRangeKeepsRecentLocationsUniqueAndBounded()
    {
        var settings = new AppSettings();
        var paths = Enumerable.Range(
                0,
                RecentScanLocationService.MaximumRecentLocations + 2)
            .Select(index => Directory.CreateDirectory(
                Path.Combine(_testRoot, index.ToString())).FullName)
            .ToList();

        RecentScanLocationService.AddRange(settings, paths);
        RecentScanLocationService.AddRange(settings, [paths[3]]);

        Assert.Equal(
            RecentScanLocationService.MaximumRecentLocations,
            settings.RecentScanPaths.Count);
        Assert.Equal(paths[3], settings.RecentScanPaths[0]);
        Assert.Equal(
            settings.RecentScanPaths.Count,
            settings.RecentScanPaths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
    }

    [Fact]
    public void PruneUnavailableKeepsExistingFilesAndFolders()
    {
        var folder = Directory.CreateDirectory(
            Path.Combine(_testRoot, "folder")).FullName;
        var file = Path.Combine(_testRoot, "file.pdf");
        File.WriteAllText(file, "test");
        var settings = new AppSettings
        {
            RecentScanPaths =
            [
                folder,
                file,
                Path.Combine(_testRoot, "missing")
            ]
        };

        Assert.True(RecentScanLocationService.PruneUnavailable(settings));
        Assert.Equal([folder, file], settings.RecentScanPaths);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }
}
