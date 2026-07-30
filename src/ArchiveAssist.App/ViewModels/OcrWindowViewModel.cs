using System.Collections.ObjectModel;
using System.IO;
using ArchiveAssist.App.Commands;
using ArchiveAssist.App.Models;
using ArchiveAssist.Core.Models;
using ArchiveAssist.Core.Services;

namespace ArchiveAssist.App.ViewModels;

public sealed class OcrWindowViewModel : ObservableObject
{
    private static readonly IReadOnlyList<OcrPageModeOption> AvailablePageModes =
    [
        new(
            PdfOcrPageMode.MissingTextOnly,
            "Only pages without text (recommended)",
            "Pages that already contain searchable text are copied without OCR or image processing."),
        new(
            PdfOcrPageMode.AllPages,
            "All pages (force OCR)",
            "Every page is rasterized and OCRed. Use this only when an existing text layer is incorrect.")
    ];

    private readonly string _sourceRoot;
    private readonly IReadOnlyList<ReportRow> _sourceRows;
    private readonly IPdfOcrService _ocrService;
    private CancellationTokenSource? _cancellation;
    private string _outputFolder = string.Empty;
    private string _statusText = "Checking OCR setup...";
    private string _errorMessage = string.Empty;
    private string _currentFileName = string.Empty;
    private string _currentStage = string.Empty;
    private bool _isBusy;
    private bool _isProcessing;
    private bool _engineReady;
    private bool _outputFolderIsValid;
    private int _completedFiles;
    private OcrPageModeOption _selectedPageMode = AvailablePageModes[0];

    public OcrWindowViewModel(
        string sourceRoot,
        IReadOnlyList<ReportRow> sourceRows,
        IPdfOcrService ocrService,
        string? initialOutputFolder = null)
    {
        _sourceRoot = Path.GetFullPath(sourceRoot);
        _sourceRows = sourceRows;
        _ocrService = ocrService;
        CheckSetupCommand = new(CheckSetupAsync, () => !IsBusy);
        StartOcrCommand = new(StartOcrAsync, CanStartOcr);
        CancelCommand = new(Cancel, () => IsProcessing);
        if (!string.IsNullOrWhiteSpace(initialOutputFolder) && Directory.Exists(initialOutputFolder))
        {
            try { SetOutputFolder(initialOutputFolder); }
            catch (InvalidOperationException) { RebuildQueue(); }
        }
        else RebuildQueue();
    }

    public event EventHandler<PdfOcrBatchResult>? BatchCompleted;

    public ObservableCollection<OcrDependencyStatus> Dependencies { get; } = [];
    public ObservableCollection<OcrQueueItem> Queue { get; } = [];
    public ObservableCollection<PdfOcrFileResult> Results { get; } = [];
    public IReadOnlyList<OcrPageModeOption> PageModes => AvailablePageModes;
    public AsyncRelayCommand CheckSetupCommand { get; }
    public AsyncRelayCommand StartOcrCommand { get; }
    public RelayCommand CancelCommand { get; }

    public string SourceRoot => _sourceRoot;
    public int TotalFiles => _sourceRows.Count;
    public double ProgressPercent => TotalFiles == 0 ? 0 : CompletedFiles * 100d / TotalFiles;
    public string SetupInstructions =>
        "Archive Assist requires 64-bit OCRmyPDF and Tesseract. Ghostscript is optional for this standard-PDF workflow. " +
        "The app writes verified copies to the selected output folder and never changes source PDFs.";

    public OcrPageModeOption SelectedPageMode
    {
        get => _selectedPageMode;
        set
        {
            if (!SetProperty(ref _selectedPageMode, value)) return;
            OnPropertyChanged(nameof(PageModeDescription));
        }
    }

    public string PageModeDescription => SelectedPageMode.Description;

    public string OutputFolder
    {
        get => _outputFolder;
        private set => SetProperty(ref _outputFolder, value);
    }

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }
    public string CurrentFileName { get => _currentFileName; private set => SetProperty(ref _currentFileName, value); }
    public string CurrentStage { get => _currentStage; private set => SetProperty(ref _currentStage, value); }
    public bool EngineReady { get => _engineReady; private set => SetProperty(ref _engineReady, value); }
    public bool OutputFolderIsValid { get => _outputFolderIsValid; private set => SetProperty(ref _outputFolderIsValid, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(CanSelectOutputFolder));
            RefreshCommands();
        }
    }

    public bool IsProcessing
    {
        get => _isProcessing;
        private set
        {
            if (!SetProperty(ref _isProcessing, value)) return;
            OnPropertyChanged(nameof(CanSelectOutputFolder));
            RefreshCommands();
        }
    }

    public bool CanSelectOutputFolder => !IsBusy;

    public int CompletedFiles
    {
        get => _completedFiles;
        private set
        {
            if (!SetProperty(ref _completedFiles, value)) return;
            OnPropertyChanged(nameof(ProgressPercent));
        }
    }

    public void SetOutputFolder(string folder)
    {
        var fullPath = Path.GetFullPath(folder);
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException($"Output folder not found: {fullPath}");
        if (PathsOverlap(_sourceRoot, fullPath))
            throw new InvalidOperationException("Choose an output folder that is separate from the source archive.");

        OutputFolder = fullPath;
        OutputFolderIsValid = true;
        ErrorMessage = string.Empty;
        RebuildQueue();
        StatusText = $"Ready to create {_sourceRows.Count:N0} searchable PDF copy/copies.";
        RefreshCommands();
    }

    public async Task CheckSetupAsync()
    {
        IsBusy = true;
        ErrorMessage = string.Empty;
        StatusText = "Checking OCRmyPDF, Tesseract, and Ghostscript...";
        try
        {
            var status = await _ocrService.CheckAvailabilityAsync();
            Dependencies.Clear();
            foreach (var dependency in status.Dependencies) Dependencies.Add(dependency);
            EngineReady = status.IsReady;
            StatusText = status.Summary;
        }
        catch (Exception exception)
        {
            EngineReady = false;
            ErrorMessage = exception.Message;
            StatusText = "OCR setup check failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StartOcrAsync()
    {
        if (!CanStartOcr()) return;

        IsBusy = true;
        IsProcessing = true;
        ErrorMessage = string.Empty;
        Results.Clear();
        CompletedFiles = 0;
        CurrentFileName = string.Empty;
        CurrentStage = "Starting";
        StatusText = SelectedPageMode.Mode == PdfOcrPageMode.MissingTextOnly
            ? "Creating searchable copies from pages without text..."
            : "Force-OCRing every page into searchable copies...";
        _cancellation = new();

        var progress = new Progress<PdfOcrProgress>(update =>
        {
            CompletedFiles = update.CompletedFiles;
            CurrentFileName = update.CurrentFileName;
            CurrentStage = update.Stage;
            if (update.Result is not null) Results.Add(update.Result);
        });

        try
        {
            var result = await _ocrService.CreateSearchableCopiesAsync(
                new(_sourceRoot, OutputFolder, _sourceRows, SelectedPageMode.Mode),
                progress,
                _cancellation.Token);
            CompletedFiles = result.Results.Count;
            StatusText = result.WasCancelled
                ? $"OCR cancelled. Kept {result.CompletedCount + result.WarningCount:N0} completed output(s)."
                : $"OCR complete: {result.CompletedCount:N0} verified, {result.WarningCount:N0} with warnings, " +
                  $"{result.SkippedCount:N0} skipped, {result.FailedCount:N0} failed.";
            BatchCompleted?.Invoke(this, result);
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            StatusText = "OCR processing could not start.";
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            IsProcessing = false;
            IsBusy = false;
            CurrentFileName = string.Empty;
            CurrentStage = string.Empty;
        }
    }

    public void Cancel()
    {
        _cancellation?.Cancel();
        StatusText = "Cancelling OCR and removing the current partial output...";
        CancelCommand.NotifyCanExecuteChanged();
    }

    private bool CanStartOcr() => !IsBusy && EngineReady && OutputFolderIsValid && _sourceRows.Count > 0;

    private void RebuildQueue()
    {
        Queue.Clear();
        foreach (var row in _sourceRows)
        {
            var relativeFolder = row.RelativeFolder == "." ? string.Empty : row.RelativeFolder;
            var outputPath = string.IsNullOrWhiteSpace(OutputFolder)
                ? "Choose an output folder"
                : Path.Combine(OutputFolder, relativeFolder, row.FileName);
            Queue.Add(new(row, row.FileName, row.RelativeFolder, row.NonOcrPageNumbersLabel, outputPath));
        }
    }

    private void RefreshCommands()
    {
        CheckSetupCommand.NotifyCanExecuteChanged();
        StartOcrCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private static bool PathsOverlap(string first, string second)
    {
        var firstPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));
        var secondPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        return IsSameOrNested(firstPath, secondPath) || IsSameOrNested(secondPath, firstPath);
    }

    private static bool IsSameOrNested(string candidate, string root) =>
        string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
