using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ArchiveAssist.App.Models;
using ArchiveAssist.App.Services;
using ArchiveAssist.App.ViewModels;
using ArchiveAssist.Core.Models;
using ArchiveAssist.Core.Services;

namespace ArchiveAssist.App;

public partial class PdfInPlaceWindow : Window
{
    private readonly IScanPathPicker _pathPicker;
    private readonly IFileRecoveryService _recoveryService;
    private readonly AppSettings _settings;
    private string? _lastRecoveryPointId;

    public PdfInPlaceWindow(
        PdfInPlaceOperation operation,
        IPdfInPlaceService service,
        IScanPathPicker pathPicker,
        IFileRecoveryService recoveryService,
        AppSettings settings)
    {
        InitializeComponent();
        _pathPicker = pathPicker;
        _recoveryService = recoveryService;
        _settings = settings;
        ViewModel = new(operation, service);
        ViewModel.BatchCompleted += ViewModel_BatchCompleted;
        DataContext = ViewModel;
        Loaded += PdfInPlaceWindow_Loaded;
        Closing += PdfInPlaceWindow_Closing;
    }

    public PdfInPlaceWindowViewModel ViewModel { get; }

    private async void PdfInPlaceWindow_Loaded(object sender, RoutedEventArgs e) =>
        await ViewModel.CheckSetupAsync();

    private void SelectFiles_Click(object sender, RoutedEventArgs e)
    {
        var currentPaths = ViewModel.Queue.Select(item => item.FullPath).ToList();
        var initialDirectory = currentPaths
            .Select(path => Path.GetDirectoryName(path))
            .FirstOrDefault(Directory.Exists);
        var selection = _pathPicker.PickPaths(currentPaths, initialDirectory);
        if (selection is null) return;
        ViewModel.ReplaceSelection(selection);
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e) =>
        ViewModel.RemoveFiles(QueueGrid.SelectedItems.Cast<PdfInPlaceQueueItem>());

    private void Clear_Click(object sender, RoutedEventArgs e) => ViewModel.ClearFiles();

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanStart) return;

        var message = ViewModel.IsOptimization
            ? $"Archive Assist will optimize and may replace {ViewModel.TotalFiles:N0} original PDF file(s).\n\n" +
              $"Mode: {ViewModel.SelectedOptimization.Name}\n" +
              "Each output is verified, and an original is kept whenever the output is not smaller.\n" +
              "Every successful replacement keeps a managed recovery point.\n" +
              "Digital signatures may become invalid.\n\nContinue?"
            : $"Archive Assist will force-OCR every page and replace {ViewModel.TotalFiles:N0} original PDF file(s).\n\n" +
              "This rasterizes every page. Forms, digital signatures, bookmarks, and interactive content may be flattened or lost.\n" +
              "Each replacement is verified and its original is retained in Recovery Center.\n\nContinue?";
        var answer = MessageBox.Show(
            this,
            message,
            ViewModel.IsOptimization ? "Confirm in-place optimization" : "Confirm whole-file OCR",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        ProcessingTabs.SelectedIndex = 1;
        await ViewModel.ProcessAsync();
    }

    private void ViewModel_BatchCompleted(object? sender, PdfInPlaceBatchResult result)
    {
        var operation = ViewModel.IsOptimization ? "Optimization" : "Whole-file OCR";
        var saved = ViewModel.IsOptimization ? $"\n{FormatBytes(result.BytesSaved)} saved" : string.Empty;
        CompletionSummaryText.Text =
            (result.WasCancelled ? "Processing cancelled\n" : $"{operation} complete\n") +
            $"{result.ReplacedCount:N0} original PDF(s) replaced\n" +
            $"{result.UnchangedCount:N0} original PDF(s) kept\n" +
            $"{result.WarningCount:N0} completed with warnings\n" +
            $"{result.FailedCount:N0} failed" +
            saved;
        _lastRecoveryPointId = result.Results
            .LastOrDefault(item => !string.IsNullOrWhiteSpace(item.RecoveryPointId))
            ?.RecoveryPointId;
        UndoLastButton.IsEnabled = _lastRecoveryPointId is not null;
        CompletionPanel.Visibility = Visibility.Visible;
    }

    private async void UndoLast_Click(object sender, RoutedEventArgs e)
    {
        if (_lastRecoveryPointId is null)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            "Restore the most recently replaced PDF?\n\n" +
            "Archive Assist will retain its current version as another recovery point first.",
            "Undo last replacement",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        UndoLastButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() =>
                _recoveryService.Restore(
                    _lastRecoveryPointId,
                    _settings.RecoveryRetentionDays));
            CompletionSummaryText.Text =
                $"Restored {Path.GetFileName(result.RestoredPath)}.\n" +
                "Its processed version remains available in Recovery Center.";
            _lastRecoveryPointId = null;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Undo failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            UndoLastButton.IsEnabled = true;
        }
    }

    private void RecoveryCenter_Click(object sender, RoutedEventArgs e)
    {
        new RecoveryCenterWindow(
            _recoveryService,
            _settings.RecoveryRetentionDays)
        {
            Owner = this
        }.ShowDialog();
    }

    private void DismissCompletion_Click(object sender, RoutedEventArgs e) =>
        CompletionPanel.Visibility = Visibility.Collapsed;

    private void PdfInPlaceWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!ViewModel.IsProcessing) return;
        e.Cancel = true;
        ViewModel.Cancel();
        MessageBox.Show(
            this,
            "Archive Assist is cancelling processing and cleaning up the current temporary output. Close this window after cancellation finishes.",
            "Cancelling",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1024 * 1024
            ? $"{bytes / (1024d * 1024d):N1} MB"
            : $"{bytes / 1024d:N1} KB";
}
