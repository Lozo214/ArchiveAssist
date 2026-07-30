using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
using ArchiveAssist.App.Commands;
using ArchiveAssist.App.Models;
using ArchiveAssist.App.Services;
using ArchiveAssist.Core.Models;
using ArchiveAssist.Core.Services;

namespace ArchiveAssist.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly IScanPathPicker _scanPathPicker;
    private readonly IPdfFolderScanner _scanner;
    private readonly ISettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly Stopwatch _operationStopwatch = new();
    private readonly DispatcherTimer _elapsedTimer;
    private PdfDiscoveryResult? _discovery;
    private CancellationTokenSource? _scanCancellation;
    private IReadOnlyList<string> _selectedPaths = [];
    private string _folderPath;
    private string _selectionText;
    private string _statusText = "Select one or more files or folders to begin.";
    private string _errorMessage = string.Empty;
    private string _discoveryWarningsText = string.Empty;
    private string _currentFileName = string.Empty;
    private string _selectedReportFilter = "All files";
    private string _reportSearchText = string.Empty;
    private string _scanPhaseText = string.Empty;
    private string _elapsedText = "0:00";
    private string _completionTitle = string.Empty;
    private string _completionDetails = string.Empty;
    private bool _isBusy;
    private bool _isScanning;
    private bool _isCompletionVisible;
    private int _completedFiles;
    private int _totalFiles;
    private int _pdfCount;
    private int _totalPages;
    private int _documents;
    private int _maps;
    private int _photos;
    private int _photoBacks;
    private int _productionTotal;
    private int _errorCount;
    private int _warningCount;
    private int _filesOverPageLimit;
    private int _largeScanFiles;
    private int _skippedFiles;
    private int _pagesWithText;
    private int _pagesWithoutText;
    private int _filesWithNonOcrPages;
    private int _nonSearchablePdfs;
    private long _totalFileSizeBytes;
    private int _maxPagesPerPdf;
    private PageSizePreset _selectedThreshold;
    private PdfQaMode _selectedQaMode;

    public MainWindowViewModel(
        IScanPathPicker scanPathPicker,
        IPdfFolderScanner scanner,
        ISettingsService settingsService,
        AppSettings settings)
    {
        _scanPathPicker = scanPathPicker;
        _scanner = scanner;
        _settingsService = settingsService;
        _settings = settings;
        if (settings.SettingsVersion < 1)
        {
            settings.ThresholdName = PageSizePreset.StandardScannerName;
            settings.SettingsVersion = 1;
            settingsService.Save(settings);
        }
        _folderPath = Directory.Exists(settings.LastFolder) ? Path.GetFullPath(settings.LastFolder) : string.Empty;
        _selectionText = FormatSelection(_selectedPaths);
        _maxPagesPerPdf = settings.MaxPagesPerPdf > 0 ? settings.MaxPagesPerPdf : 500;
        _selectedThreshold = PageSizePreset.BuiltIn.FirstOrDefault(
            preset => string.Equals(preset.Name, settings.ThresholdName, StringComparison.OrdinalIgnoreCase))
            ?? PageSizePreset.Default;
        _selectedQaMode = PdfQaMode.BuiltIn.FirstOrDefault(
            mode => string.Equals(mode.Name, settings.QaModeName, StringComparison.OrdinalIgnoreCase))
            ?? PdfQaMode.StandardQa;
        _selectedReportFilter = ReportFilters.Contains(settings.ReportFilterName)
            ? settings.ReportFilterName
            : "All files";
        _reportSearchText = settings.ReportSearchText ?? string.Empty;
        _elapsedTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            (_, _) => UpdateElapsedTime(),
            Dispatcher.CurrentDispatcher);
        _elapsedTimer.Stop();

        ReportView = CollectionViewSource.GetDefaultView(Results);
        ReportView.Filter = MatchesReportFilter;
        SelectPathsCommand = new(SelectPathsAsync, () => !IsBusy);
        ScanCommand = new(ScanAsync, () => !IsBusy && _discovery?.Files.Count > 0);
        CancelCommand = new(CancelScan, () => IsScanning);
    }

    public event EventHandler<ScanCompletedEventArgs>? ScanCompleted;

    public event EventHandler? SelectionChanged;

    public ObservableCollection<DiscoveredFile> DiscoveredFiles { get; } = [];
    public ObservableCollection<ReportRow> Results { get; } = [];
    public ObservableCollection<SummaryMetric> SummaryItems { get; } = [];
    public ObservableCollection<FileStructureNode> FileStructureRoots { get; } = [];
    public ICollectionView ReportView { get; }
    public IReadOnlyList<PageSizePreset> PageSizePresets => PageSizePreset.BuiltIn;
    public IReadOnlyList<PdfQaMode> QaModes => PdfQaMode.BuiltIn;
    public IReadOnlyList<string> ReportFilters { get; } =
    [
        "All files", "PDFs", "Photos", "Photo backs", "Files with warnings", "Large scans",
        "Non-OCR files", "Over page limit", "PDF errors", "Skipped files"
    ];
    public IReadOnlyList<string> QuickReportFilters { get; } =
    [
        "All files", "Files with warnings", "Non-OCR files", "Large scans", "PDF errors"
    ];
    public AsyncRelayCommand SelectPathsCommand { get; }
    public AsyncRelayCommand ScanCommand { get; }
    public RelayCommand CancelCommand { get; }

    public string FolderPath { get => _folderPath; private set => SetProperty(ref _folderPath, value); }
    public string SelectionText { get => _selectionText; private set => SetProperty(ref _selectionText, value); }
    public IReadOnlyList<string> SelectedPaths => _selectedPaths;
    public bool HasSelection => _selectedPaths.Count > 0;
    public bool HasDiscoveredFiles => DiscoveredFiles.Count > 0;
    public bool HasResults => Results.Count > 0;
    public bool HasVisibleReportRows => VisibleReportCount > 0;
    public bool HasFileStructure => FileStructureRoots.Count > 0;
    public int VisibleReportCount => ReportView.Cast<object>().Count();
    public string SelectionSummary => BuildSelectionSummary(_selectedPaths);
    public string CountContextLabel => BuildCountContextLabel(_selectedPaths);
    public string DiscoveryTabHeader => $"Discovery ({DiscoveredFiles.Count:N0})";
    public string ReportTabHeader => $"Report ({Results.Count:N0})";
    public string FileStructureTabHeader =>
        $"File Structure ({CountFolders(FileStructureRoots):N0})";
    public string SourceRootPath => _discovery?.RootPath ?? FolderPath;
    public string? SingleSelectedFolder => _selectedPaths.Count == 1 && Directory.Exists(_selectedPaths[0])
        ? _selectedPaths[0]
        : null;
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }
    public string DiscoveryWarningsText { get => _discoveryWarningsText; private set => SetProperty(ref _discoveryWarningsText, value); }
    public string CurrentFileName { get => _currentFileName; private set => SetProperty(ref _currentFileName, value); }
    public string ScanPhaseText { get => _scanPhaseText; private set => SetProperty(ref _scanPhaseText, value); }
    public string ElapsedText { get => _elapsedText; private set => SetProperty(ref _elapsedText, value); }
    public string ProgressDetailsText =>
        TotalFiles == 0
            ? $"Elapsed {ElapsedText}"
            : $"{CompletedFiles:N0} of {TotalFiles:N0} files  \u00B7  " +
              $"{ProgressPercent:0}%  \u00B7  Elapsed {ElapsedText}";
    public bool IsProgressIndeterminate => IsBusy && (!IsScanning || TotalFiles == 0);
    public bool IsCompletionVisible
    {
        get => _isCompletionVisible;
        private set => SetProperty(ref _isCompletionVisible, value);
    }
    public string CompletionTitle
    {
        get => _completionTitle;
        private set => SetProperty(ref _completionTitle, value);
    }
    public string CompletionDetails
    {
        get => _completionDetails;
        private set => SetProperty(ref _completionDetails, value);
    }

    public string ReportSearchText
    {
        get => _reportSearchText;
        set
        {
            if (!SetProperty(ref _reportSearchText, value ?? string.Empty))
            {
                return;
            }

            _settings.ReportSearchText = _reportSearchText;
            RefreshReportView(
                string.IsNullOrWhiteSpace(_reportSearchText)
                    ? "Search cleared."
                    : $"Searching report for “{_reportSearchText}”.");
        }
    }

    public string SelectedReportFilter
    {
        get => _selectedReportFilter;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || !ReportFilters.Contains(value) ||
                !SetProperty(ref _selectedReportFilter, value)) return;
            _settings.ReportFilterName = value;
            _settingsService.Save(_settings);
            RefreshReportView();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsProgressIndeterminate));
            RefreshCommands();
        }
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (!SetProperty(ref _isScanning, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsProgressIndeterminate));
            RefreshCommands();
        }
    }

    public int CompletedFiles
    {
        get => _completedFiles;
        private set
        {
            if (!SetProperty(ref _completedFiles, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(ProgressDetailsText));
        }
    }

    public int TotalFiles
    {
        get => _totalFiles;
        private set
        {
            if (!SetProperty(ref _totalFiles, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(ProgressDetailsText));
            OnPropertyChanged(nameof(IsProgressIndeterminate));
        }
    }

    public double ProgressPercent => TotalFiles == 0 ? 0 : CompletedFiles * 100d / TotalFiles;

    public PageSizePreset SelectedThreshold
    {
        get => _selectedThreshold;
        set
        {
            if (value is null || !SetProperty(ref _selectedThreshold, value)) return;
            _settings.ThresholdName = value.Name;
            _settingsService.Save(_settings);
        }
    }

    public int MaxPagesPerPdf
    {
        get => _maxPagesPerPdf;
        set
        {
            if (value <= 0 || !SetProperty(ref _maxPagesPerPdf, value)) return;
            _settings.MaxPagesPerPdf = value;
            _settingsService.Save(_settings);
        }
    }

    public PdfQaMode SelectedQaMode
    {
        get => _selectedQaMode;
        set
        {
            if (value is null || !SetProperty(ref _selectedQaMode, value)) return;
            OnPropertyChanged(nameof(QaModeDescription));
            _settings.QaModeName = value.Name;
            _settingsService.Save(_settings);
        }
    }

    public string QaModeDescription => SelectedQaMode.Description;

    public int PdfCount { get => _pdfCount; private set => SetProperty(ref _pdfCount, value); }
    public int TotalPages { get => _totalPages; private set => SetProperty(ref _totalPages, value); }
    public int Documents { get => _documents; private set => SetProperty(ref _documents, value); }
    public int Maps { get => _maps; private set => SetProperty(ref _maps, value); }
    public int Photos { get => _photos; private set => SetProperty(ref _photos, value); }
    public int PhotoBacks { get => _photoBacks; private set => SetProperty(ref _photoBacks, value); }
    public int ProductionTotal { get => _productionTotal; private set => SetProperty(ref _productionTotal, value); }
    public int ErrorCount { get => _errorCount; private set => SetProperty(ref _errorCount, value); }
    public int WarningCount { get => _warningCount; private set => SetProperty(ref _warningCount, value); }
    public int FilesOverPageLimit { get => _filesOverPageLimit; private set => SetProperty(ref _filesOverPageLimit, value); }
    public int LargeScanFiles { get => _largeScanFiles; private set => SetProperty(ref _largeScanFiles, value); }
    public int SkippedFiles { get => _skippedFiles; private set => SetProperty(ref _skippedFiles, value); }
    public int PagesWithText { get => _pagesWithText; private set => SetProperty(ref _pagesWithText, value); }
    public int PagesWithoutText { get => _pagesWithoutText; private set => SetProperty(ref _pagesWithoutText, value); }
    public int FilesWithNonOcrPages { get => _filesWithNonOcrPages; private set => SetProperty(ref _filesWithNonOcrPages, value); }
    public int NonSearchablePdfs { get => _nonSearchablePdfs; private set => SetProperty(ref _nonSearchablePdfs, value); }

    public long TotalFileSizeBytes
    {
        get => _totalFileSizeBytes;
        private set { if (SetProperty(ref _totalFileSizeBytes, value)) OnPropertyChanged(nameof(TotalFileSizeLabel)); }
    }

    public string TotalFileSizeLabel => FormatFileSize(TotalFileSizeBytes);

    public IReadOnlyList<ReportRow> VisibleReportRows() => ReportView.Cast<ReportRow>().ToList();

    public string BuildClipboardText(IReadOnlyList<ReportRow> rows)
    {
        var lines = new List<string>();
        if (_settings.IncludeClipboardHeaders)
        {
            lines.Add("File Name\tDocuments\tMaps\tPhotos\tPhoto Backs\tTotal");
        }

        lines.AddRange(rows.Select(row => string.Join("\t",
            row.FileName,
            row.Documents.ToString(),
            row.Maps.ToString(),
            row.Photos.ToString(),
            row.PhotoBacks.ToString(),
            row.Total.ToString())));
        StatusText = $"Copied {rows.Count:N0} report row(s) for Excel.";
        return string.Join(Environment.NewLine, lines);
    }

    public void ApplyReportFilter(string filter)
    {
        if (ReportFilters.Contains(filter))
        {
            SelectedReportFilter = filter;
        }
    }

    public void ClearReportFilters()
    {
        _reportSearchText = string.Empty;
        OnPropertyChanged(nameof(ReportSearchText));
        _settings.ReportSearchText = string.Empty;
        if (SelectedReportFilter != "All files")
        {
            SelectedReportFilter = "All files";
            return;
        }

        RefreshReportView("Report filters cleared.");
    }

    public void DismissCompletion() => IsCompletionVisible = false;

    public void ReloadPreferences()
    {
        SelectedThreshold = PageSizePreset.BuiltIn.FirstOrDefault(
            preset => string.Equals(
                preset.Name,
                _settings.ThresholdName,
                StringComparison.OrdinalIgnoreCase))
            ?? PageSizePreset.Default;
        MaxPagesPerPdf = _settings.MaxPagesPerPdf > 0
            ? _settings.MaxPagesPerPdf
            : 500;
        SelectedQaMode = PdfQaMode.BuiltIn.FirstOrDefault(
            mode => string.Equals(
                mode.Name,
                _settings.QaModeName,
                StringComparison.OrdinalIgnoreCase))
            ?? PdfQaMode.StandardQa;
    }

    public Task ReplaceSelectionAsync(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return DiscoverSelectionAsync(paths.ToList());
    }

    public bool ApplySummaryMetric(SummaryMetric metric)
    {
        if (metric.ReportFilter is null) return false;
        SelectedReportFilter = metric.ReportFilter;
        return true;
    }

    public void SetFileStructureExpanded(bool expanded)
    {
        foreach (var root in FileStructureRoots) SetExpanded(root, expanded);
        StatusText = expanded ? "Expanded the complete file structure." : "Collapsed the file structure.";
    }

    public void SetStatus(string message) => StatusText = message;

    private async Task SelectPathsAsync()
    {
        var paths = _scanPathPicker.PickPaths(_selectedPaths, FolderPath);
        if (paths is null || paths.Count == 0)
        {
            return;
        }

        await DiscoverSelectionAsync(paths);
    }

    private async Task DiscoverSelectionAsync(IReadOnlyList<string> paths)
    {
        if (IsBusy || paths.Count == 0)
        {
            return;
        }

        IsBusy = true;
        IsCompletionVisible = false;
        ErrorMessage = string.Empty;
        DiscoveryWarningsText = string.Empty;
        StatusText = "Discovering selected files and folders...";
        ScanPhaseText = "Discovering files and folders";
        StartOperationTimer();
        _selectedPaths = paths.ToList();
        SelectionText = FormatSelection(_selectedPaths);
        NotifySelectionChanged();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        DiscoveredFiles.Clear();
        Results.Clear();
        SummaryItems.Clear();
        FileStructureRoots.Clear();
        RefreshResultState();
        TotalFiles = 0;
        ResetSummary();

        try
        {
            _discovery = await _scanner.DiscoverAsync(_selectedPaths);
            _selectedPaths = _discovery.SelectedPaths;
            FolderPath = _discovery.RootPath;
            SelectionText = FormatSelection(_selectedPaths);
            foreach (var file in _discovery.Files) DiscoveredFiles.Add(file);
            TotalFiles = _discovery.Files.Count;
            DiscoveryWarningsText = string.Join(Environment.NewLine, _discovery.Warnings);
            _settings.LastFolder = FolderPath;
            RecentScanLocationService.AddRange(_settings, _selectedPaths);
            _settingsService.Save(_settings);
            NotifySelectionChanged();
            RefreshResultState();

            StatusText = _discovery.Files.Count == 0
                ? "No supported or reportable files were found in the selection."
                : $"Preview ready: {_discovery.PdfCount:N0} PDFs, {_discovery.PhotoCount:N0} photos, " +
                  $"{_discovery.PhotoBackCount:N0} photo backs, {_discovery.SkippedCount:N0} skipped.";
        }
        catch (Exception exception)
        {
            _discovery = null;
            ErrorMessage = exception.Message;
            StatusText = "Selection discovery failed.";
        }
        finally
        {
            StopOperationTimer();
            ScanPhaseText = string.Empty;
            IsBusy = false;
            RefreshCommands();
        }
    }

    private async Task ScanAsync()
    {
        if (_discovery is null) return;

        Results.Clear();
        SummaryItems.Clear();
        FileStructureRoots.Clear();
        RefreshResultState();
        ResetSummary();
        TotalFiles = _discovery.Files.Count;
        IsBusy = true;
        IsScanning = true;
        IsCompletionVisible = false;
        ErrorMessage = string.Empty;
        StatusText = "Building report...";
        ScanPhaseText = SelectedQaMode == PdfQaMode.FastCountOnly
            ? "Reading PDF metadata and counting files"
            : "Reading PDF metadata and performing QA checks";
        StartOperationTimer();
        _scanCancellation = new();
        PdfScanOutcome? completedOutcome = null;

        var progress = new Progress<PdfScanProgress>(update =>
        {
            Results.Add(update.Result);
            CompletedFiles = update.CompletedFiles;
            CurrentFileName = update.CurrentFileName;
            ScanPhaseText = update.Result.Kind switch
            {
                ArchiveFileKind.Pdf when SelectedQaMode == PdfQaMode.FastCountOnly =>
                    "Reading PDF metadata",
                ArchiveFileKind.Pdf => "Reading PDF metadata and performing QA checks",
                ArchiveFileKind.Photo or ArchiveFileKind.PhotoBack => "Counting photo files",
                _ => "Classifying files"
            };
            UpdateSummary(Results);
            RefreshResultState();
        });

        try
        {
            var outcome = await _scanner.ScanAsync(
                _discovery,
                new ScanOptions(SelectedThreshold, MaxPagesPerPdf, SelectedQaMode),
                progress,
                _scanCancellation.Token);
            ScanPhaseText = "Building report and file structure";
            UpdateSummary(outcome.Results, refreshSummaryItems: true);
            BuildFileStructure(outcome.Results);
            RefreshResultState();
            StatusText = outcome.WasCancelled
                ? $"Scan cancelled. Kept {outcome.Results.Count:N0} completed report row(s)."
                : outcome.Summary.ErrorCount == 0
                    ? "Report complete. Select rows and press Ctrl+C to copy them for Excel."
                    : $"Report complete with {outcome.Summary.ErrorCount:N0} PDF error(s).";
            if (!outcome.WasCancelled)
            {
                completedOutcome = outcome;
                ShowCompletion(outcome.Summary);
            }
        }
        catch (Exception exception)
        {
            UpdateSummary(Results, refreshSummaryItems: true);
            BuildFileStructure(Results);
            RefreshResultState();
            ErrorMessage = exception.Message;
            StatusText = "Scan failed. Completed report rows were retained.";
        }
        finally
        {
            StopOperationTimer();
            _scanCancellation.Dispose();
            _scanCancellation = null;
            IsScanning = false;
            IsBusy = false;
            CurrentFileName = string.Empty;
            ScanPhaseText = string.Empty;
        }

        if (completedOutcome is not null)
            ScanCompleted?.Invoke(this, new ScanCompletedEventArgs(completedOutcome.Summary));
    }

    private void CancelScan()
    {
        _scanCancellation?.Cancel();
        StatusText = "Cancelling after the current PDF...";
        CancelCommand.NotifyCanExecuteChanged();
    }

    private bool MatchesReportFilter(object item)
    {
        if (item is not ReportRow row)
        {
            return false;
        }

        var matchesCategory = SelectedReportFilter switch
        {
            "PDFs" => row.Kind == ArchiveFileKind.Pdf,
            "Photos" => row.Kind == ArchiveFileKind.Photo,
            "Photo backs" => row.Kind == ArchiveFileKind.PhotoBack,
            "Files with warnings" => row.Kind != ArchiveFileKind.Skipped && row.HasWarning,
            "Large scans" => row.IsLargeScan,
            "Non-OCR files" => row.Kind == ArchiveFileKind.Pdf &&
                               (row.PagesWithoutText > 0 || row.SearchableText is false),
            "Over page limit" => row.OverPageLimit,
            "PDF errors" => row.Kind == ArchiveFileKind.Pdf && !row.IsSuccessful,
            "Skipped files" => row.Kind == ArchiveFileKind.Skipped,
            _ => true
        };
        if (!matchesCategory || string.IsNullOrWhiteSpace(ReportSearchText))
        {
            return matchesCategory;
        }

        var search = ReportSearchText.Trim();
        return Contains(row.FileName, search) ||
               Contains(row.RelativeFolder, search) ||
               Contains(row.FullPath, search) ||
               Contains(row.TypeLabel, search) ||
               Contains(row.IssuesLabel, search);
    }

    private void UpdateSummary(IEnumerable<ReportRow> results, bool refreshSummaryItems = false)
    {
        var rows = results.ToList();
        PdfCount = rows.Count(row => row.Kind == ArchiveFileKind.Pdf);
        TotalPages = rows.Sum(row => row.PageCount ?? 0);
        Documents = rows.Sum(row => row.Documents);
        Maps = rows.Sum(row => row.Maps);
        Photos = rows.Sum(row => row.Photos);
        PhotoBacks = rows.Sum(row => row.PhotoBacks);
        ProductionTotal = rows.Sum(row => row.Total);
        ErrorCount = rows.Count(row => row.Kind == ArchiveFileKind.Pdf && !row.IsSuccessful);
        WarningCount = rows.Count(row => row.Kind != ArchiveFileKind.Skipped && row.HasWarning);
        FilesOverPageLimit = rows.Count(row => row.OverPageLimit);
        LargeScanFiles = rows.Count(row => row.IsLargeScan);
        SkippedFiles = rows.Count(row => row.Kind == ArchiveFileKind.Skipped);
        PagesWithText = rows.Sum(row => row.PagesWithText);
        PagesWithoutText = rows.Sum(row => row.PagesWithoutText);
        FilesWithNonOcrPages = rows.Count(row => row.PagesWithoutText > 0);
        NonSearchablePdfs = rows.Count(row => row.SearchableText is false);
        TotalFileSizeBytes = rows.Sum(row => row.FileSizeBytes);
        if (refreshSummaryItems) RefreshSummaryItems();
    }

    private void RefreshSummaryItems()
    {
        SummaryItems.Clear();
        SummaryItems.Add(new("Total PDFs", PdfCount.ToString("N0")));
        SummaryItems.Add(new("Total Pages", TotalPages.ToString("N0")));
        SummaryItems.Add(new("Documents", Documents.ToString("N0")));
        SummaryItems.Add(new("Maps", Maps.ToString("N0"), "Large scans"));
        SummaryItems.Add(new("Photos", Photos.ToString("N0"), "Photos"));
        SummaryItems.Add(new("Photo Backs", PhotoBacks.ToString("N0"), "Photo backs"));
        SummaryItems.Add(new("Production Total", ProductionTotal.ToString("N0")));
        SummaryItems.Add(new("Total File Size", TotalFileSizeLabel));
        SummaryItems.Add(new("QA Mode", SelectedQaMode.Name));
        SummaryItems.Add(new("Files with Warnings", WarningCount.ToString("N0"), "Files with warnings"));
        SummaryItems.Add(new("Non-Searchable PDFs", NonSearchablePdfs.ToString("N0"), "Non-OCR files"));
        SummaryItems.Add(new("Files With Non-OCR Pages", FilesWithNonOcrPages.ToString("N0"), "Non-OCR files"));
        SummaryItems.Add(new("Files Over Page Limit", FilesOverPageLimit.ToString("N0"), "Over page limit"));
        SummaryItems.Add(new("Files with Large Pages", LargeScanFiles.ToString("N0"), "Large scans"));
        SummaryItems.Add(new("Skipped Non-PDF Files", SkippedFiles.ToString("N0"), "Skipped files"));
        SummaryItems.Add(new("PDF Errors", ErrorCount.ToString("N0"), "PDF errors"));
    }

    private void BuildFileStructure(IEnumerable<ReportRow> rows)
    {
        FileStructureRoots.Clear();
        if (string.IsNullOrWhiteSpace(FolderPath)) return;
        FileStructureRoots.Add(FileStructureBuilder.Build(FolderPath, rows));
    }

    private void ResetSummary()
    {
        CompletedFiles = PdfCount = TotalPages = Documents = Maps = Photos = PhotoBacks = 0;
        ProductionTotal = ErrorCount = WarningCount = FilesOverPageLimit = 0;
        LargeScanFiles = SkippedFiles = 0;
        PagesWithText = PagesWithoutText = FilesWithNonOcrPages = NonSearchablePdfs = 0;
        TotalFileSizeBytes = 0;
        CurrentFileName = string.Empty;
    }

    private void RefreshCommands()
    {
        SelectPathsCommand.NotifyCanExecuteChanged();
        ScanCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private void RefreshReportView(string? status = null)
    {
        ReportView.Refresh();
        OnPropertyChanged(nameof(VisibleReportCount));
        OnPropertyChanged(nameof(HasVisibleReportRows));
        if (!string.IsNullOrWhiteSpace(status))
        {
            StatusText = status;
        }
        else
        {
            StatusText =
                $"Showing {VisibleReportCount:N0} of {Results.Count:N0} report rows.";
        }
    }

    private void RefreshResultState()
    {
        OnPropertyChanged(nameof(HasDiscoveredFiles));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(HasVisibleReportRows));
        OnPropertyChanged(nameof(HasFileStructure));
        OnPropertyChanged(nameof(VisibleReportCount));
        OnPropertyChanged(nameof(DiscoveryTabHeader));
        OnPropertyChanged(nameof(ReportTabHeader));
        OnPropertyChanged(nameof(FileStructureTabHeader));
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedPaths));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(CountContextLabel));
    }

    private void StartOperationTimer()
    {
        _operationStopwatch.Restart();
        ElapsedText = "0:00";
        _elapsedTimer.Start();
        OnPropertyChanged(nameof(ProgressDetailsText));
    }

    private void StopOperationTimer()
    {
        _operationStopwatch.Stop();
        _elapsedTimer.Stop();
        UpdateElapsedTime();
    }

    private void UpdateElapsedTime()
    {
        var elapsed = _operationStopwatch.Elapsed;
        ElapsedText = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss")
            : elapsed.ToString(@"m\:ss");
        OnPropertyChanged(nameof(ProgressDetailsText));
    }

    private void ShowCompletion(PdfScanSummary summary)
    {
        CompletionTitle = summary.ErrorCount == 0
            ? "Scan complete"
            : $"Scan complete with {summary.ErrorCount:N0} PDF error(s)";
        CompletionDetails =
            $"{summary.PdfCount:N0} PDFs \u00B7 {summary.TotalPages:N0} pages \u00B7 " +
            $"{summary.Total:N0} production items \u00B7 " +
            $"{summary.WarningCount:N0} warning files";
        IsCompletionVisible = true;
    }

    private static bool Contains(string? value, string search) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Contains(search, StringComparison.OrdinalIgnoreCase);

    private static string BuildSelectionSummary(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return "Drop files or folders here, or choose Select files/folders.";
        }

        var folderCount = paths.Count(Directory.Exists);
        var fileCount = paths.Count(File.Exists);
        var parts = new List<string>();
        if (folderCount > 0)
        {
            parts.Add($"{folderCount:N0} folder{(folderCount == 1 ? string.Empty : "s")}");
        }

        if (fileCount > 0)
        {
            parts.Add($"{fileCount:N0} individual file{(fileCount == 1 ? string.Empty : "s")}");
        }

        return parts.Count == 0
            ? $"{paths.Count:N0} selected location(s)"
            : string.Join(" and ", parts);
    }

    private static string BuildCountContextLabel(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return "No selection";
        }

        if (paths.Count > 1)
        {
            return $"{paths.Count:N0} selected locations";
        }

        var path = paths[0].TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private static int CountFolders(IEnumerable<FileStructureNode> nodes) =>
        nodes.Sum(node =>
            (node.IsFolder ? 1 : 0) +
            CountFolders(node.Children));

    private static void SetExpanded(FileStructureNode node, bool expanded)
    {
        node.IsExpanded = expanded;
        foreach (var child in node.Children) SetExpanded(child, expanded);
    }

    private static string FormatFileSize(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824d:N1} GB",
        >= 1_048_576 => $"{bytes / 1_048_576d:N1} MB",
        >= 1_024 => $"{bytes / 1_024d:N1} KB",
        _ => $"{bytes:N0} B"
    };

    private static string FormatSelection(IReadOnlyList<string> paths) => paths.Count switch
    {
        0 => string.Empty,
        1 => paths[0],
        _ => $"{paths.Count:N0} items selected: {string.Join("; ", paths)}"
    };
}
