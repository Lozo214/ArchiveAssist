using ArchiveAssist.App.Models;
using ArchiveAssist.App.Services;
using System.IO;

namespace ArchiveAssist.App.Tests;

public sealed class RecentPdfServiceTests : IDisposable
{
    private readonly string _testRoot = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(),
        "ArchiveAssist.Recent.Tests",
        Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public void AddMovesAnExistingPdfToTheFrontAndCapsTheList()
    {
        var settings = new AppSettings();
        var paths = Enumerable.Range(0, RecentPdfService.MaximumRecentPdfs + 2)
            .Select(index => Path.Combine(_testRoot, $"{index}.pdf"))
            .ToList();

        foreach (var path in paths)
        {
            RecentPdfService.Add(settings, path);
        }

        RecentPdfService.Add(settings, paths[3]);

        Assert.Equal(RecentPdfService.MaximumRecentPdfs, settings.RecentPdfPaths.Count);
        Assert.Equal(Path.GetFullPath(paths[3]), settings.RecentPdfPaths[0]);
        Assert.Equal(
            settings.RecentPdfPaths.Count,
            settings.RecentPdfPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void PruneUnavailableKeepsOnlyExistingPdfFiles()
    {
        var availablePdf = Path.Combine(_testRoot, "available.pdf");
        var textFile = Path.Combine(_testRoot, "notes.txt");
        File.WriteAllText(availablePdf, "test");
        File.WriteAllText(textFile, "test");
        var settings = new AppSettings
        {
            RecentPdfPaths =
            [
                availablePdf,
                availablePdf.ToUpperInvariant(),
                Path.Combine(_testRoot, "missing.pdf"),
                textFile
            ]
        };

        var changed = RecentPdfService.PruneUnavailable(settings);

        Assert.True(changed);
        Assert.Single(settings.RecentPdfPaths);
        Assert.Equal(availablePdf, settings.RecentPdfPaths[0]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }
}
