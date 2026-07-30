using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using ArchiveAssist.App.Services;
using ArchiveAssist.Core.Models;
using ArchiveAssist.Core.Services;

namespace ArchiveAssist.App;

public partial class EqualizerWindow : Window
{
    private string? _sourceFolder;
    private int _maxPagesPerPdf;
    private readonly IPdfPageEqualizer _equalizer;
    private readonly IFolderPicker _folderPicker;
    private CancellationTokenSource? _cancellation;
    private EqualizationPreview? _preview;
    private string? _lastOutputRoot;

    public EqualizerWindow(
        string? initialSourceFolder,
        int maxPagesPerPdf,
        IPdfPageEqualizer equalizer,
        IFolderPicker folderPicker)
    {
        _sourceFolder = !string.IsNullOrWhiteSpace(initialSourceFolder) && Directory.Exists(initialSourceFolder)
            ? Path.GetFullPath(initialSourceFolder)
            : null;
        _maxPagesPerPdf = maxPagesPerPdf > 0 ? maxPagesPerPdf : 500;
        _equalizer = equalizer;
        _folderPicker = folderPicker;
        InitializeComponent();
        SourceFolderTextBox.Text = _sourceFolder ?? string.Empty;
        MaxPagesTextBox.Text = _maxPagesPerPdf.ToString(CultureInfo.CurrentCulture);
        Loaded += EqualizerWindow_Loaded;
    }

    private async void EqualizerWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= EqualizerWindow_Loaded;
        if (_sourceFolder is null)
        {
            OperationStatusText.Text = "Select a source folder and page limit, then preview the equalization plan.";
            return;
        }
        await PreviewAsync(showInputErrors: false);
    }

    private async void SelectSourceFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = _folderPicker.PickFolder(_sourceFolder, "Select the PDF source folder to equalize");
        if (string.IsNullOrWhiteSpace(folder)) return;

        _sourceFolder = Path.GetFullPath(folder);
        SourceFolderTextBox.Text = _sourceFolder;
        _lastOutputRoot = null;
        OpenOutputButton.IsEnabled = false;
        await PreviewAsync(showInputErrors: true);
    }

    private async void PreviewButton_Click(object sender, RoutedEventArgs e) =>
        await PreviewAsync(showInputErrors: true);

    private async Task PreviewAsync(bool showInputErrors)
    {
        if (!TryApplyInputs(showInputErrors)) return;
        var sourceFolder = _sourceFolder!;
        var maxPagesPerPdf = _maxPagesPerPdf;
        _preview = null;
        FolderPlansGrid.ItemsSource = null;
        ClearSummary();
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        SetBusy(true, "Checking PDF page counts...");

        var progress = new Progress<EqualizationProgress>(UpdateProgress);
        try
        {
            var preview = await _equalizer.PreviewAsync(
                sourceFolder,
                maxPagesPerPdf,
                progress,
                cancellation.Token);
            _preview = preview;
            FolderPlansGrid.ItemsSource = preview.Folders;
            ShowSummary(preview);
            OperationStatusText.Text = preview.SourceFiles == 0
                ? "No PDFs were found in the selected folder."
                : preview.HasWork
                    ? $"Preview ready. {preview.FoldersRequiringWork:N0} folder(s) will be rebuilt; source PDFs remain untouched."
                    : $"All {preview.SourceFiles:N0} PDFs are within the {maxPagesPerPdf:N0}-page limit. No output is needed.";
        }
        catch (OperationCanceledException)
        {
            OperationStatusText.Text = "Preview cancelled.";
        }
        catch (Exception exception)
        {
            OperationStatusText.Text = "Preview failed.";
            MessageBox.Show(this, exception.Message, "Could not preview equalization", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            SetBusy(false);
        }
    }

    private async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        if (_preview is not { HasWork: true } preview) return;

        var sourceFolder = _sourceFolder!;
        var initialParent = Path.GetDirectoryName(sourceFolder) ?? sourceFolder;
        var outputParent = _folderPicker.PickFolder(
            initialParent,
            "Choose where to create the Equalized PDFs folder");
        if (string.IsNullOrWhiteSpace(outputParent)) return;

        var outputRoot = Path.Combine(outputParent, "Equalized PDFs");
        if (Directory.Exists(outputRoot) || File.Exists(outputRoot))
        {
            MessageBox.Show(
                this,
                $"Choose another location or rename the existing output path:\n\n{outputRoot}",
                "Output folder already exists",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        var confirmation =
            $"Source PDFs: {preview.SourceFiles:N0}\n" +
            $"Source pages: {preview.SourcePages:N0}\n" +
            $"Files over limit: {preview.FilesOverLimit:N0}\n" +
            $"Expected output PDFs: {preview.ExpectedOutputFiles:N0}\n\n" +
            "Each folder will be processed independently and the subfolder structure will be preserved. " +
            "Pages remain in alphabetical file order. The original PDFs will not be changed.\n\n" +
            $"Output:\n{outputRoot}";
        if (MessageBox.Show(
                this,
                confirmation,
                "Create equalized PDFs?",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        SetBusy(true, "Rechecking source PDFs...");
        var progress = new Progress<EqualizationProgress>(UpdateProgress);

        try
        {
            var result = await _equalizer.EqualizeAsync(
                sourceFolder,
                outputRoot,
                _maxPagesPerPdf,
                progress,
                cancellation.Token);

            if (result.OutputFiles == 0)
            {
                OperationStatusText.Text = "Equalization is no longer needed. No files were created.";
                await PreviewAsync(showInputErrors: false);
                return;
            }

            _lastOutputRoot = result.OutputRoot;
            OpenOutputButton.IsEnabled = true;
            OperationStatusText.Text = $"Complete. Created {result.OutputFiles:N0} PDFs and a page-source manifest.";
            MessageBox.Show(
                this,
                $"Created {result.OutputFiles:N0} PDFs in:\n\n{result.OutputRoot}\n\n" +
                "equalization_manifest.csv records every output page's source. The original PDFs were not changed.",
                "Equalization complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            OperationStatusText.Text = "Equalization cancelled. Partial output was removed.";
        }
        catch (Exception exception)
        {
            OperationStatusText.Text = "Equalization failed. Partial output was removed when possible.";
            MessageBox.Show(this, exception.Message, "Equalization failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            SetBusy(false);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cancellation is null || _cancellation.IsCancellationRequested) return;
        _cancellation.Cancel();
        CancelButton.IsEnabled = false;
        OperationStatusText.Text = "Cancelling and cleaning up partial output...";
    }

    private void OpenOutputButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastOutputRoot)) return;
        try
        {
            PathLauncher.Open(_lastOutputRoot);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Could not open output", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_cancellation is null) return;
        e.Cancel = true;
        CancelButton_Click(this, new RoutedEventArgs());
    }

    private void UpdateProgress(EqualizationProgress update)
    {
        OperationProgressBar.IsIndeterminate = update.TotalFiles == 0;
        OperationProgressBar.Maximum = Math.Max(update.TotalFiles, 1);
        OperationProgressBar.Value = Math.Min(update.CompletedFiles, OperationProgressBar.Maximum);
        OperationStatusText.Text = update.Message;
    }

    private void SetBusy(bool busy, string? message = null)
    {
        PreviewButton.IsEnabled = !busy;
        SelectSourceFolderButton.IsEnabled = !busy;
        MaxPagesTextBox.IsEnabled = !busy;
        RunButton.IsEnabled = !busy && _preview is { HasWork: true };
        CancelButton.IsEnabled = busy && _cancellation is { IsCancellationRequested: false };
        if (busy)
        {
            OperationProgressBar.IsIndeterminate = true;
            OperationProgressBar.Value = 0;
        }
        if (!string.IsNullOrWhiteSpace(message)) OperationStatusText.Text = message;
    }

    private void ShowSummary(EqualizationPreview preview)
    {
        SourceFilesText.Text = preview.SourceFiles.ToString("N0");
        SourcePagesText.Text = preview.SourcePages.ToString("N0");
        OverLimitText.Text = preview.FilesOverLimit.ToString("N0");
        FoldersText.Text = preview.FoldersRequiringWork.ToString("N0");
        OutputFilesText.Text = preview.ExpectedOutputFiles.ToString("N0");
    }

    private void ClearSummary()
    {
        SourceFilesText.Text = SourcePagesText.Text = OverLimitText.Text = FoldersText.Text = OutputFilesText.Text = "-";
    }

    private void MaxPagesTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!IsLoaded || _cancellation is not null) return;
        _preview = null;
        RunButton.IsEnabled = false;
        ClearSummary();
        OperationStatusText.Text = "Page limit changed. Preview the plan again before creating output.";
    }

    private bool TryApplyInputs(bool showErrors)
    {
        string? message = null;
        if (string.IsNullOrWhiteSpace(_sourceFolder) || !Directory.Exists(_sourceFolder))
        {
            message = "Select an existing source folder before previewing equalization.";
        }
        else if (!int.TryParse(
                     MaxPagesTextBox.Text,
                     NumberStyles.Integer,
                     CultureInfo.CurrentCulture,
                     out var maxPages) || maxPages <= 0)
        {
            message = "Enter a positive whole number for Max pages.";
        }
        else
        {
            _maxPagesPerPdf = maxPages;
            return true;
        }

        _preview = null;
        RunButton.IsEnabled = false;
        ClearSummary();
        OperationStatusText.Text = message;
        if (showErrors)
            MessageBox.Show(this, message, "Equalizer settings required", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }
}
