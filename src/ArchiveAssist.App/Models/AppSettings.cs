using ArchiveAssist.Core.Models;

namespace ArchiveAssist.App.Models;

public sealed class AppSettings
{
    public string LastFolder { get; set; } = string.Empty;
    public string ThresholdName { get; set; } = PageSizePreset.StandardScannerName;
    public int MaxPagesPerPdf { get; set; } = 500;
    public string QaModeName { get; set; } = "Standard QA";
    public string LastOcrOutputFolder { get; set; } = string.Empty;
    public double WindowWidth { get; set; } = 1180;
    public double WindowHeight { get; set; } = 760;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool IsMaximized { get; set; }
    public double EditorWindowWidth { get; set; } = 1280;
    public double EditorWindowHeight { get; set; } = 860;
    public double? EditorWindowLeft { get; set; }
    public double? EditorWindowTop { get; set; }
    public bool IsEditorMaximized { get; set; }
    public double EditorThumbnailZoom { get; set; } = 150;
    public double EditorDetailZoom { get; set; } = 100;
    public string EditorViewMode { get; set; } = "ThumbnailGrid";
    public string EditorDetailFitMode { get; set; } = "FitPage";
    public List<string> RecentPdfPaths { get; set; } = [];
    public List<string> RecentScanPaths { get; set; } = [];
    public bool HasSeenEditorWelcome { get; set; }
    public bool HasSeenMainWelcome { get; set; }
    public bool IncludeClipboardHeaders { get; set; } = true;
    public bool MainAdvancedSettingsExpanded { get; set; }
    public string ReportFilterName { get; set; } = "All files";
    public string ReportSearchText { get; set; } = string.Empty;
    public Dictionary<string, double> ReportColumnWidths { get; set; } = [];
    public string ReportSortProperty { get; set; } = string.Empty;
    public string ReportSortDirection { get; set; } = string.Empty;
    public string FileSafetyMode { get; set; } = FileSafetyModes.SafeInPlace;
    public int RecoveryRetentionDays { get; set; } = 30;
    public int SettingsVersion { get; set; }
}
