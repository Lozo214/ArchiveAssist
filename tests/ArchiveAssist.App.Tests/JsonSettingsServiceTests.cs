using ArchiveAssist.App.Models;
using ArchiveAssist.App.Services;
using System.IO;

namespace ArchiveAssist.App.Tests;

public sealed class JsonSettingsServiceTests
{
    [Fact]
    public void EditorLayoutPreferencesRoundTrip()
    {
        var testRoot = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(),
            "ArchiveAssist.App.Tests",
            Guid.NewGuid().ToString("N"))).FullName;
        var settingsPath = Path.Combine(testRoot, "settings.json");

        try
        {
            var service = new JsonSettingsService(settingsPath);
            service.Save(new AppSettings
            {
                EditorWindowWidth = 1440,
                EditorWindowHeight = 900,
                EditorThumbnailZoom = 325,
                EditorDetailZoom = 175,
                EditorViewMode = "Page",
                EditorDetailFitMode = "None",
                IsEditorMaximized = true,
                RecentPdfPaths = [@"C:\Archive\one.pdf", @"C:\Archive\two.pdf"],
                RecentScanPaths = [@"C:\Archive", @"C:\Intake"],
                HasSeenEditorWelcome = true,
                HasSeenMainWelcome = true,
                IncludeClipboardHeaders = false,
                MainAdvancedSettingsExpanded = true,
                ReportFilterName = "Files with warnings",
                ReportSearchText = "map",
                ReportColumnWidths = new() { ["File Name"] = 333 },
                ReportSortProperty = "FileName",
                ReportSortDirection = "Ascending",
                FileSafetyMode = FileSafetyModes.AlwaysAsk,
                RecoveryRetentionDays = 90
            });

            var restored = service.Load();

            Assert.Equal(1440, restored.EditorWindowWidth);
            Assert.Equal(900, restored.EditorWindowHeight);
            Assert.Equal(325, restored.EditorThumbnailZoom);
            Assert.Equal(175, restored.EditorDetailZoom);
            Assert.Equal("Page", restored.EditorViewMode);
            Assert.Equal("None", restored.EditorDetailFitMode);
            Assert.True(restored.IsEditorMaximized);
            Assert.Equal(
                [@"C:\Archive\one.pdf", @"C:\Archive\two.pdf"],
                restored.RecentPdfPaths);
            Assert.True(restored.HasSeenEditorWelcome);
            Assert.Equal([@"C:\Archive", @"C:\Intake"], restored.RecentScanPaths);
            Assert.True(restored.HasSeenMainWelcome);
            Assert.False(restored.IncludeClipboardHeaders);
            Assert.True(restored.MainAdvancedSettingsExpanded);
            Assert.Equal("Files with warnings", restored.ReportFilterName);
            Assert.Equal("map", restored.ReportSearchText);
            Assert.Equal(333, restored.ReportColumnWidths["File Name"]);
            Assert.Equal("FileName", restored.ReportSortProperty);
            Assert.Equal("Ascending", restored.ReportSortDirection);
            Assert.Equal(FileSafetyModes.AlwaysAsk, restored.FileSafetyMode);
            Assert.Equal(90, restored.RecoveryRetentionDays);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }
}
