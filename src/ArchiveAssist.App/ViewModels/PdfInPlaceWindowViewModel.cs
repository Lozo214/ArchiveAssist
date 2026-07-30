using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using ArchiveAssist.App.Commands;
using ArchiveAssist.App.Models;
using ArchiveAssist.Core.Models;
using ArchiveAssist.Core.Services;

namespace ArchiveAssist.App.ViewModels;

public sealed class PdfInPlaceWindowViewModel : ObservableObject
{
    private static readonly IReadOnlyList<PdfOptimizationOption> AvailableOptimizationLevels =
    [
        new(
            PdfOptimizationLevel.Lossless,
            "Level 1 - Lossless (recommended)",
            "Applies safe, lossless cleanup and compression. This is the default."),
        new(
            PdfOptimizationLevel.Balanced,
            "Level 2 - Balanced",
            "Uses stronger image compression and may reduce image quality."),
        new(
            PdfOptimizationLevel.Aggressive,
            "Level 3 - Aggressive",
            "Prioritizes smaller files and may noticeably reduce image quality.")
    ];

    private readonly IPdfInPlaceService _service;
    private readonly Stopwatch _elapsedStopwatch = new();
    private readonly DispatcherTimer _elapsedTimer;
    private CancellationTokenSource? _cancellation;
    private PdfOptimizationOption _selectedOptimization = AvailableOptimizationLevels[0];
    private string _statusText = "Select PDFs or folders to begin.";
    private string _errorMessage = string.Empty;
    private string _currentFileName = string.Empty;
    private string _currentStage = string.Empty;
    private string _currentDetail = string.Empty;
    private string _elapsedText = string.Empty;
    private bool _isBusy;
    private bool _isProcessing;
    private bool _engineReady;
    private int _completedFiles;
    private double? _currentFileProgress;

    public PdfInPlaceWindowViewModel(PdfInPlaceOperation operation, IPdfInPlaceService service)
    {
        Operation = operation;
        _service = service;
        CheckSetupCommand = new(CheckSetupAsync, () => !IsBusy);
        CancelCommand = new(Cancel, () => IsProcessing);
        _elapsedTimer = new(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _elapsedTimer.Tick += (_, _) => UpdateElapsedText();
    }

    public event EventHandler<PdfInPlaceBatchResult>? BatchCompleted;

    public PdfInPlaceOperation Operation { get; }
    public bool IsOptimization => Operation == PdfInPlaceOperation.Optimize;
    public string WindowTitle => IsOptimization
        ? "Optimize PDFs - Archive Assist"
        : "OCR Entire PDFs In Place - Archive Assist";
    public string Heading => IsOptimization ? "Optimize PDFs In Place" : "OCR Entire PDFs In Place";
    public string Description => IsOptimization
        ? "Reduce PDF file sizes with OCRmyPDF's optimizer without performing OCR."
        : "Rebuild every page with OCR and replace each original only after the output is verified.";
    public string WarningText => IsOptimization
        ? "These files will be replaced in place. Digital signatures may become invalid. Lossless Level 1 is the safest choice."
        : "Every page will be rasterized and OCRed. Forms, signatures, bookmarks, or interactive content may be flattened or lost.";
    public string SetupInstructions => IsOptimization
        ? "Optimization requires OCRmyPDF. Tesseract is not used because OCR is disabled for this workflow."
        : "Whole-file OCR requires OCRmyPDF and Tesseract. Ghostscript is optional for standard PDF output.";
    public string StartButtonText => IsOptimization ? "Optimize in place" : "OCR in place";

    public ObservableCollection<OcrDependencyStatus> Dependencies { get; } = [];
    public ObservableCollection<PdfInPlaceQueueItem> Queue { get; } = [];
    public ObservableCollection<PdfInPlaceFileResult> Results { get; } = [];
    public IReadOnlyList<PdfOptimizationOption> OptimizationLevels => AvailableOptimizationLevels;
    public AsyncRelayCommand CheckSetupCommand { get; }
    public RelayCommand CancelCommand { get; }

    public PdfOptimizationOption SelectedOptimization
    {
        get => _selectedOptimization;
        set
        {
            if (!SetProperty(ref _selectedOptimization, value)) return;
            OnPropertyChanged(nameof(OptimizationDescription));
        }
    }

    public string OptimizationDescription => SelectedOptimization.Description;
    public int TotalFiles => Queue.Count;
    public double ProgressPercent
    {
        get
        {
            if (TotalFiles == 0) return 0;
            var currentFraction = Math.Clamp(CurrentFileProgress ?? 0, 0, 0.99);
            return Math.Min(100, (CompletedFiles + currentFraction) * 100d / TotalFiles);
        }
    }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }
    public string CurrentFileName { get => _currentFileName; private set => SetProperty(ref _currentFileName, value); }
    public string CurrentStage { get => _currentStage; private set => SetProperty(ref _currentStage, value); }
    public string CurrentDetail { get => _currentDetail; private set => SetProperty(ref _currentDetail, value); }
    public string ElapsedText { get => _elapsedText; private set => SetProperty(ref _elapsedText, value); }
    public bool EngineReady { get => _engineReady; private set => SetProperty(ref _engineReady, value); }
    public double? CurrentFileProgress
    {
        get => _currentFileProgress;
        private set
        {
            if (!SetProperty(ref _currentFileProgress, value)) return;
            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(IsProgressIndeterminate));
        }
    }

    public bool IsProgressIndeterminate => IsProcessing && CurrentFileProgress is null;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(CanEditQueue));
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(IsProgressIndeterminate));
            RefreshCommands();
        }
    }

    public bool IsProcessing
    {
        get => _isProcessing;
        private set
        {
            if (!SetProperty(ref _isProcessing, value)) return;
            OnPropertyChanged(nameof(CanEditQueue));
            OnPropertyChanged(nameof(CanStart));
            RefreshCommands();
        }
    }

    public int CompletedFiles
    {
        get => _completedFiles;
        private set
        {
            if (!SetProperty(ref _completedFiles, value)) return;
            OnPropertyChanged(nameof(ProgressPercent));
        }
    }

    public bool CanEditQueue => !IsBusy;
    public bool CanStart => !IsBusy && EngineReady && Queue.Count > 0;

    public void ReplaceSelection(IEnumerable<string> selectedPaths)
    {
        if (!CanEditQueue) return;

        var pdfs = ExpandPdfPaths(selectedPaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Queue.Clear();
        foreach (var path in pdfs) Queue.Add(new(path));
        Results.Clear();
        CompletedFiles = 0;
        ErrorMessage = string.Empty;
        QueueChanged();
    }

    public void RemoveFiles(IEnumerable<PdfInPlaceQueueItem> items)
    {
        if (!CanEditQueue) return;
        foreach (var item in items.ToList()) Queue.Remove(item);
        Results.Clear();
        CompletedFiles = 0;
        QueueChanged();
    }

    public void ClearFiles()
    {
        if (!CanEditQueue) return;
        Queue.Clear();
        Results.Clear();
        CompletedFiles = 0;
        QueueChanged();
    }

    public async Task CheckSetupAsync()
    {
        IsBusy = true;
        ErrorMessage = string.Empty;
        StatusText = IsOptimization ? "Checking OCRmyPDF..." : "Checking OCRmyPDF and OCR dependencies...";
        try
        {
            var status = await _service.CheckInPlaceAvailabilityAsync(Operation);
            Dependencies.Clear();
            foreach (var dependency in status.Dependencies) Dependencies.Add(dependency);
            EngineReady = status.IsReady;
            StatusText = status.Summary;
        }
        catch (Exception exception)
        {
            EngineReady = false;
            ErrorMessage = exception.Message;
            StatusText = "Setup check failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ProcessAsync()
    {
        if (!CanStart) return;

        var paths = Queue.Select(item => item.FullPath).ToList();
        IsBusy = true;
        IsProcessing = true;
        ErrorMessage = string.Empty;
        Results.Clear();
        CompletedFiles = 0;
        CurrentFileName = string.Empty;
        CurrentStage = "Starting";
        CurrentDetail = "Preparing the first PDF...";
        CurrentFileProgress = null;
        _elapsedStopwatch.Restart();
        UpdateElapsedText();
        _elapsedTimer.Start();
        StatusText = IsOptimization
            ? "Optimizing selected PDFs..."
            : "Force-OCRing every page in the selected PDFs...";
        _cancellation = new();

        var progress = new Progress<PdfInPlaceProgress>(update =>
        {
            CompletedFiles = update.CompletedFiles;
            CurrentFileName = update.CurrentFileName;
            CurrentStage = update.Stage;
            CurrentFileProgress = update.Result is null ? update.CurrentFileProgress : 0;
            CurrentDetail = update.Detail ??
                            (update.Result is not null ? update.Result.Message : string.Empty);
            if (update.Result is not null) Results.Add(update.Result);
        });

        try
        {
            var result = await _service.ProcessInPlaceAsync(
                new(paths, Operation, SelectedOptimization.Level),
                progress,
                _cancellation.Token);
            CompletedFiles = result.Results.Count;
            StatusText = result.WasCancelled
                ? $"Cancelled after {result.Results.Count:N0} file(s). Originals not yet processed were left unchanged."
                : $"{(IsOptimization ? "Optimization" : "OCR")} complete: {result.ReplacedCount:N0} replaced, " +
                  $"{result.UnchangedCount:N0} originals kept, {result.FailedCount:N0} failed.";
            RefreshQueueSizes(paths);
            BatchCompleted?.Invoke(this, result);
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            StatusText = "Processing could not start.";
        }
        finally
        {
            _elapsedTimer.Stop();
            _elapsedStopwatch.Stop();
            UpdateElapsedText();
            _cancellation.Dispose();
            _cancellation = null;
            IsProcessing = false;
            IsBusy = false;
            CurrentFileName = string.Empty;
            CurrentStage = string.Empty;
            CurrentFileProgress = null;
        }
    }

    public void Cancel()
    {
        _cancellation?.Cancel();
        StatusText = "Cancelling and removing the current temporary output...";
        CancelCommand.NotifyCanExecuteChanged();
    }

    private void RefreshQueueSizes(IReadOnlyList<string> paths)
    {
        Queue.Clear();
        foreach (var path in paths.Where(File.Exists)) Queue.Add(new(path));
        QueueChanged(updateStatus: false);
    }

    private void QueueChanged(bool updateStatus = true)
    {
        OnPropertyChanged(nameof(TotalFiles));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(CanStart));
        if (updateStatus)
        {
            StatusText = Queue.Count == 0
                ? "Select PDFs or folders to begin."
                : $"{Queue.Count:N0} PDF file(s) ready.";
        }
    }

    private void RefreshCommands()
    {
        CheckSetupCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private void UpdateElapsedText()
    {
        var elapsed = _elapsedStopwatch.Elapsed;
        ElapsedText = elapsed.TotalHours >= 1
            ? $"Elapsed {elapsed:hh\\:mm\\:ss}"
            : $"Elapsed {elapsed:mm\\:ss}";
    }

    private static IEnumerable<string> ExpandPdfPaths(IEnumerable<string> selectedPaths)
    {
        foreach (var selectedPath in selectedPaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var fullPath = Path.GetFullPath(selectedPath);
            if (File.Exists(fullPath))
            {
                if (string.Equals(Path.GetExtension(fullPath), ".pdf", StringComparison.OrdinalIgnoreCase))
                    yield return fullPath;
                continue;
            }

            if (!Directory.Exists(fullPath)) continue;
            foreach (var pdfPath in EnumeratePdfFilesSafely(fullPath))
                yield return pdfPath;
        }
    }

    private static IEnumerable<string> EnumeratePdfFilesSafely(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            string[] files;
            string[] folders;
            try
            {
                files = Directory.GetFiles(folder, "*.pdf", SearchOption.TopDirectoryOnly);
                folders = Directory.GetDirectories(folder);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files) yield return Path.GetFullPath(file);
            foreach (var child in folders) pending.Push(child);
        }
    }
}
