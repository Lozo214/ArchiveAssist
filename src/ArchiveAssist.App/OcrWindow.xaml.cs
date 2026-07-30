using System.ComponentModel;
using System.IO;
using System.Windows;
using ArchiveAssist.App.Services;
using ArchiveAssist.App.ViewModels;
using ArchiveAssist.Core.Models;
using ArchiveAssist.Core.Services;

namespace ArchiveAssist.App;

public partial class OcrWindow : Window
{
    private readonly IFolderPicker _folderPicker;

    public OcrWindow(
        string sourceRoot,
        IReadOnlyList<ReportRow> rows,
        IPdfOcrService ocrService,
        IFolderPicker folderPicker,
        string? initialOutputFolder = null)
    {
        InitializeComponent();
        _folderPicker = folderPicker;
        ViewModel = new(sourceRoot, rows, ocrService, initialOutputFolder);
        ViewModel.BatchCompleted += ViewModel_BatchCompleted;
        DataContext = ViewModel;
        Loaded += OcrWindow_Loaded;
        Closing += OcrWindow_Closing;
    }

    public OcrWindowViewModel ViewModel { get; }
    public string OutputFolder => ViewModel.OutputFolder;

    private async void OcrWindow_Loaded(object sender, RoutedEventArgs e) => await ViewModel.CheckSetupAsync();

    private void SelectOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        var initial = Directory.Exists(ViewModel.OutputFolder)
            ? ViewModel.OutputFolder
            : Path.GetDirectoryName(ViewModel.SourceRoot);
        var folder = _folderPicker.PickFolder(initial, "Select a separate folder for searchable PDF copies");
        if (folder is null) return;

        try { ViewModel.SetOutputFolder(folder); }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Invalid OCR output folder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ViewModel_BatchCompleted(object? sender, PdfOcrBatchResult result)
    {
        if (result.WasCancelled) return;
        var message =
            $"{result.CompletedCount:N0} verified searchable copies\n" +
            $"{result.WarningCount:N0} completed with verification warnings\n" +
            $"{result.SkippedCount:N0} skipped\n" +
            $"{result.FailedCount:N0} failed\n\n" +
            "Source PDFs were not changed.";
        MessageBox.Show(this, message, "OCR complete", MessageBoxButton.OK,
            result.FailedCount == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void OcrWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!ViewModel.IsProcessing) return;
        e.Cancel = true;
        ViewModel.Cancel();
        MessageBox.Show(this, "Archive Assist is cancelling OCR and cleaning up the partial output. Close this window after cancellation finishes.",
            "Cancelling OCR", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
