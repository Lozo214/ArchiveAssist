using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ArchiveAssist.App.Models;
using ArchiveAssist.App.Services;
using ArchiveAssist.App.ViewModels;
using ArchiveAssist.Core.Models;
using ArchiveAssist.Core.Services;
using Microsoft.Win32;

namespace ArchiveAssist.App;

public partial class MainWindow : Window
{
    private readonly JsonSettingsService _settingsService = new();
    private readonly AppSettings _settings;
    private readonly IPdfMetadataReader _metadataReader = new PdfMetadataReader();
    private readonly IFileRecoveryService _recoveryService = FileRecoveryService.CreateDefault();
    private readonly DispatcherTimer _toastTimer;

    public MainWindow()
    {
        _settings = _settingsService.Load();
        _settings.ReportColumnWidths ??= [];
        _settings.RecentScanPaths ??= [];
        _settings.FileSafetyMode = FileSafetyModes.Normalize(_settings.FileSafetyMode);
        if (_settings.RecoveryRetentionDays is not (0 or 7 or 30 or 90))
        {
            _settings.RecoveryRetentionDays = 30;
        }
        TryCleanupExpiredRecoveryPoints();
        InitializeComponent();
        _toastTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.5)
        };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastNotification.Visibility = Visibility.Collapsed;
        };
        var viewModel = new MainWindowViewModel(
            new ScanPathPicker(), new PdfFolderScanner(), _settingsService, _settings);
        viewModel.ScanCompleted += ViewModel_ScanCompleted;
        viewModel.SelectionChanged += ViewModel_SelectionChanged;
        DataContext = viewModel;
        Loaded += RestoreWindowSettings;
        Closing += SaveWindowSettings;
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private void PreferencesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { IsBusy: true })
        {
            MessageBox.Show(
                this,
                "Wait for the current operation to finish before changing preferences.",
                "Archive Assist is busy",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (new PreferencesWindow(_settings, _settingsService)
            {
                Owner = this
            }.ShowDialog() != true)
        {
            return;
        }

        ViewModel?.ReloadPreferences();
        AdvancedSettingsExpander.IsExpanded = _settings.MainAdvancedSettingsExpanded;
        ApplyReportLayout();
        MainWelcomePanel.Visibility = _settings.HasSeenMainWelcome
            ? Visibility.Collapsed
            : Visibility.Visible;
        TryCleanupExpiredRecoveryPoints();
        ShowToast("Preferences saved.");
    }

    private void TryCleanupExpiredRecoveryPoints()
    {
        try
        {
            _recoveryService.CleanupExpired();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Recovery-index problems must not prevent Archive Assist from opening.
        }
    }

    private void RecoveryCenterMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { IsBusy: true })
        {
            MessageBox.Show(
                this,
                "Wait for the current operation to finish before restoring files.",
                "Archive Assist is busy",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        new RecoveryCenterWindow(
            _recoveryService,
            _settings.RecoveryRetentionDays)
        {
            Owner = this
        }.ShowDialog();
    }

    private void WholeFileOcrMenuItem_Click(object sender, RoutedEventArgs e) =>
        OpenInPlaceWindow(PdfInPlaceOperation.WholeFileOcr);

    private void OptimizePdfsMenuItem_Click(object sender, RoutedEventArgs e) =>
        OpenInPlaceWindow(PdfInPlaceOperation.Optimize);

    private void OpenInPlaceWindow(PdfInPlaceOperation operation)
    {
        if (ViewModel is not { } viewModel) return;
        if (viewModel.IsBusy)
        {
            MessageBox.Show(
                this,
                "Wait for the current operation to finish before opening a PDF processing tool.",
                "Archive Assist is busy",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var processingService = new OcrMyPdfService(
            recoveryService: _recoveryService,
            recoveryRetentionDays: _settings.RecoveryRetentionDays);
        new PdfInPlaceWindow(
            operation,
            processingService,
            new PdfProcessingPathPicker(),
            _recoveryService,
            _settings)
        {
            Owner = this
        }.ShowDialog();
    }

    private void PageEqualizerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel) return;
        if (viewModel.IsBusy)
        {
            MessageBox.Show(this, "Wait for the current operation to finish before opening the equalizer.",
                "Archive Assist is busy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        new EqualizerWindow(
            viewModel.SingleSelectedFolder,
            viewModel.MaxPagesPerPdf,
            new PdfPageEqualizer(),
            new FolderPicker())
        {
            Owner = this
        }.ShowDialog();
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenPdfEditorMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open PDF in Archive Assist Editor",
            Filter = "PDF files (*.pdf)|*.pdf|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            OpenPdfEditor(dialog.FileName);
        }
    }

    private void RecentScanLocationsMenuItem_SubmenuOpened(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem)
        {
            PopulateRecentScanLocationMenu(menuItem);
        }
    }

    private async void RecentScanLocationMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string path } ||
            (!File.Exists(path) && !Directory.Exists(path)))
        {
            ViewModel?.SetStatus("That recent scan location is no longer available.");
            return;
        }

        if (ViewModel is { } viewModel)
        {
            await viewModel.ReplaceSelectionAsync([path]);
        }
    }

    private void PopulateRecentScanLocationMenu(MenuItem parent)
    {
        parent.Items.Clear();
        if (RecentScanLocationService.PruneUnavailable(_settings))
        {
            _settingsService.Save(_settings);
        }

        var paths = RecentScanLocationService.Available(_settings);
        if (paths.Count == 0)
        {
            parent.Items.Add(new MenuItem
            {
                Header = "(No recent locations)",
                IsEnabled = false
            });
            return;
        }

        for (var index = 0; index < paths.Count; index++)
        {
            var path = paths[index];
            var name = Path.GetFileName(path.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar));
            var item = new MenuItem
            {
                Header = $"_{index + 1}  {name.Replace("_", "__")}",
                ToolTip = path,
                Tag = path
            };
            item.Click += RecentScanLocationMenuItem_Click;
            parent.Items.Add(item);
        }
    }

    private void RecentPdfsMenuItem_SubmenuOpened(object sender, RoutedEventArgs e) =>
        PopulateRecentPdfMenu();

    private void RecentPdfMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string pdfPath } && File.Exists(pdfPath))
        {
            OpenPdfEditor(pdfPath);
            return;
        }

        PopulateRecentPdfMenu();
        ViewModel?.SetStatus("That recent PDF is no longer available.");
    }

    private void PopulateRecentPdfMenu()
    {
        RecentPdfsMenuItem.Items.Clear();
        if (RecentPdfService.PruneUnavailable(_settings))
        {
            _settingsService.Save(_settings);
        }

        var recentPaths = RecentPdfService.Available(_settings);
        if (recentPaths.Count == 0)
        {
            RecentPdfsMenuItem.Items.Add(new MenuItem
            {
                Header = "(No recent PDFs)",
                IsEnabled = false
            });
            return;
        }

        for (var index = 0; index < recentPaths.Count; index++)
        {
            var path = recentPaths[index];
            var menuItem = new MenuItem
            {
                Header = $"_{index + 1}  {Path.GetFileName(path).Replace("_", "__")}",
                ToolTip = path,
                Tag = path
            };
            menuItem.Click += RecentPdfMenuItem_Click;
            RecentPdfsMenuItem.Items.Add(menuItem);
        }
    }

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e) =>
        new AboutWindow { Owner = this }.ShowDialog();

    private void ShowGettingStarted_Click(object sender, RoutedEventArgs e) =>
        MainWelcomePanel.Visibility = Visibility.Visible;

    private void DismissGettingStarted_Click(object sender, RoutedEventArgs e)
    {
        MainWelcomePanel.Visibility = Visibility.Collapsed;
        _settings.HasSeenMainWelcome = true;
        _settingsService.Save(_settings);
    }

    private void AdvancedSettingsExpander_Changed(object sender, RoutedEventArgs e)
    {
        if (AdvancedSettingsExpander is null)
        {
            return;
        }

        _settings.MainAdvancedSettingsExpanded =
            AdvancedSettingsExpander.IsExpanded;
    }

    private void SelectionDropZone_DragEnter(object sender, DragEventArgs e) =>
        UpdateSelectionDropFeedback(e);

    private void SelectionDropZone_DragOver(object sender, DragEventArgs e) =>
        UpdateSelectionDropFeedback(e);

    private void SelectionDropZone_DragLeave(object sender, DragEventArgs e)
    {
        SelectionDropZone.Background = Brushes.White;
        SelectionDropZone.BorderBrush =
            new SolidColorBrush(Color.FromRgb(191, 200, 206));
    }

    private async void SelectionDropZone_Drop(object sender, DragEventArgs e)
    {
        SelectionDropZone_DragLeave(sender, e);
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) ||
            e.Data.GetData(DataFormats.FileDrop) is not string[] droppedPaths)
        {
            return;
        }

        var paths = droppedPaths
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (paths.Count == 0)
        {
            ViewModel?.SetStatus("Drop one or more available files or folders.");
            return;
        }

        if (ViewModel is { } viewModel)
        {
            await viewModel.ReplaceSelectionAsync(paths);
        }
    }

    private void UpdateSelectionDropFeedback(DragEventArgs e)
    {
        var valid = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = valid ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        SelectionDropZone.Background = valid
            ? new SolidColorBrush(Color.FromRgb(232, 241, 248))
            : Brushes.White;
        SelectionDropZone.BorderBrush = valid
            ? new SolidColorBrush(Color.FromRgb(23, 105, 170))
            : new SolidColorBrush(Color.FromRgb(191, 200, 206));
    }

    private void ShowDiscoveryPreview_Click(object sender, RoutedEventArgs e) =>
        SelectResultsTab(DiscoveryPreviewTab);

    private void ShowReport_Click(object sender, RoutedEventArgs e) =>
        SelectResultsTab(ReportTab);

    private void ShowFileStructure_Click(object sender, RoutedEventArgs e) =>
        SelectResultsTab(FileStructureTab);

    private void CompletionViewReport_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.ApplyReportFilter("All files");
        SelectResultsTab(ReportTab);
        ReportGrid.Focus();
    }

    private void CompletionViewIssues_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.ApplyReportFilter("Files with warnings");
        SelectResultsTab(ReportTab);
        ReportGrid.Focus();
    }

    private void CompletionOpenSummary_Click(object sender, RoutedEventArgs e)
    {
        SummaryGrid.Focus();
        if (SummaryGrid.Items.Count > 0)
        {
            SummaryGrid.SelectedIndex = 0;
            SummaryGrid.ScrollIntoView(SummaryGrid.SelectedItem);
        }
    }

    private void CompletionDismiss_Click(object sender, RoutedEventArgs e) =>
        ViewModel?.DismissCompletion();

    private void MainKeyboardShortcuts_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(
            this,
            "Ctrl+O   Select files and folders\n" +
            "F5   Start scan\n" +
            "Ctrl+1   Discovery Preview\n" +
            "Ctrl+2   Report\n" +
            "Ctrl+3   File Structure\n" +
            "Ctrl+F   Search the Report\n" +
            "Ctrl+C   Copy selected Report rows\n" +
            "Drag files or folders onto the selection area\n" +
            "Double-click a Report row to see its details",
            "Archive Assist keyboard shortcuts",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

    private void ClearReportFilters_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.ClearReportFilters();
        ReportSearchBox.Focus();
    }

    private void CopySelectedRowsButton_Click(object sender, RoutedEventArgs e) =>
        CopyReportRows(ReportGrid.SelectedItems.Cast<ReportRow>().ToList());

    private void CopyVisibleRowsButton_Click(object sender, RoutedEventArgs e) =>
        CopyReportRows(ViewModel?.VisibleReportRows() ?? []);

    private void CreateSearchableCopiesButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel) return;
        if (viewModel.IsBusy)
        {
            MessageBox.Show(this, "Wait for the current operation to finish before starting OCR.",
                "Archive Assist is busy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selectedRows = ReportGrid.SelectedItems.Cast<ReportRow>()
            .Where(row => row.Kind == ArchiveFileKind.Pdf && row.IsSuccessful &&
                          (row.SearchableText is false || row.PagesWithoutText > 0))
            .ToList();
        if (selectedRows.Count == 0)
        {
            MessageBox.Show(this,
                "Select one or more PDF rows flagged as non-searchable first. Use the Non-OCR files filter and Ctrl+A to select all visible flagged PDFs.",
                "Select non-searchable PDFs", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var window = new OcrWindow(
            viewModel.SourceRootPath,
            selectedRows,
            new OcrMyPdfService(),
            new FolderPicker(),
            _settings.LastOcrOutputFolder)
        {
            Owner = this
        };
        window.ShowDialog();
        if (string.IsNullOrWhiteSpace(window.OutputFolder)) return;
        _settings.LastOcrOutputFolder = window.OutputFolder;
        _settingsService.Save(_settings);
    }

    private void ReportGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        var selectedRows = ReportGrid.SelectedItems.Cast<ReportRow>().ToList();
        CopyReportRows(
            selectedRows.Count > 0
                ? selectedRows
                : ViewModel?.VisibleReportRows() ?? []);
        e.Handled = true;
    }

    private void CopyReportRows(IReadOnlyList<ReportRow> rows)
    {
        if (ViewModel is not { } viewModel || rows.Count == 0)
        {
            ViewModel?.SetStatus("There are no applicable report rows to copy.");
            return;
        }

        if (SetClipboardText(viewModel.BuildClipboardText(rows)))
        {
            ShowToast($"{rows.Count:N0} report row(s) copied for Excel.");
        }
    }

    private void ReportGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selectedRows = ReportGrid.SelectedItems.Cast<ReportRow>().ToList();
        var hasSelection = selectedRows.Count > 0;
        var singleRow = selectedRows.Count == 1 ? selectedRows[0] : null;
        ReportDetailsButton.IsEnabled = singleRow is not null;
        ReportEditButton.IsEnabled =
            singleRow is { Kind: ArchiveFileKind.Pdf } &&
            File.Exists(singleRow.FullPath);
        ReportOpenFolderButton.IsEnabled = singleRow is not null;
        CopySelectedRowsButton.IsEnabled = hasSelection;
    }

    private void ReportGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e) =>
        SelectRowUnderPointer<DataGridRow>(e.OriginalSource as DependencyObject);

    private void ReportGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is null) return;
        ShowDetails(SelectedReportRow());
    }

    private void ReportDetailsMenuItem_Click(object sender, RoutedEventArgs e) => ShowDetails(SelectedReportRow());

    private void ReportEditPdfMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var row = SelectedReportRow();
        if (row is null)
        {
            return;
        }

        if (row.Kind != ArchiveFileKind.Pdf || !File.Exists(row.FullPath))
        {
            MessageBox.Show(
                this,
                "Select an available PDF row first.",
                "Open PDF editor",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        OpenPdfEditor(row.FullPath);
    }

    private void ReportOpenFileMenuItem_Click(object sender, RoutedEventArgs e) =>
        RunPathAction(() => PathLauncher.Open(SelectedReportRow()?.FullPath ?? string.Empty));

    private void ReportOpenFolderMenuItem_Click(object sender, RoutedEventArgs e) =>
        RunPathAction(() => PathLauncher.ShowInFolder(SelectedReportRow()?.FullPath ?? string.Empty));

    private void ReportCopyNameMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var row = SelectedReportRow();
        if (row is null) return;
        SetClipboardText(row.FileName);
        ViewModel?.SetStatus($"Copied filename: {row.FileName}");
    }

    private void SummaryGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e) =>
        SelectRowUnderPointer<DataGridRow>(e.OriginalSource as DependencyObject);

    private void SummaryGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is null) return;
        ViewSelectedSummaryRows();
    }

    private void ViewSummaryRowsMenuItem_Click(object sender, RoutedEventArgs e) => ViewSelectedSummaryRows();

    private void ViewSelectedSummaryRows()
    {
        if (SummaryGrid.SelectedItem is not SummaryMetric metric || ViewModel is not { } viewModel) return;
        if (!viewModel.ApplySummaryMetric(metric)) return;
        ResultsTabs.SelectedItem = ReportTab;
        ReportGrid.Focus();
    }

    private void ExpandAllButton_Click(object sender, RoutedEventArgs e) => ViewModel?.SetFileStructureExpanded(true);

    private void CollapseAllButton_Click(object sender, RoutedEventArgs e) => ViewModel?.SetFileStructureExpanded(false);

    private void FileStructureTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item is null) return;
        item.IsSelected = true;
        item.Focus();
    }

    private async void FileStructureTree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F2)
        {
            return;
        }

        e.Handled = true;
        await RenameSelectedStructureItemAsync();
    }

    private void FileStructureTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not FileStructureNode { ReportRow: { } row }) return;
        ShowDetails(row);
        e.Handled = true;
    }

    private void StructureDetailsMenuItem_Click(object sender, RoutedEventArgs e) =>
        ShowDetails(SelectedStructureNode()?.ReportRow);

    private void StructureEditPdfMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var row = SelectedStructureNode()?.ReportRow;
        if (row is null || row.Kind != ArchiveFileKind.Pdf || !File.Exists(row.FullPath))
        {
            MessageBox.Show(
                this,
                "Select an available PDF first.",
                "Open PDF editor",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        OpenPdfEditor(row.FullPath);
    }

    private void StructureOpenMenuItem_Click(object sender, RoutedEventArgs e) =>
        RunPathAction(() => PathLauncher.Open(SelectedStructureNode()?.FullPath ?? string.Empty));

    private void StructureOpenFolderMenuItem_Click(object sender, RoutedEventArgs e) =>
        RunPathAction(() => PathLauncher.ShowInFolder(SelectedStructureNode()?.FullPath ?? string.Empty));

    private async void StructureRenameMenuItem_Click(object sender, RoutedEventArgs e) =>
        await RenameSelectedStructureItemAsync();

    private void StructureCopyNameMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var node = SelectedStructureNode();
        if (node is null) return;
        SetClipboardText(node.Name);
        ViewModel?.SetStatus($"Copied name: {node.Name}");
    }

    private async Task RenameSelectedStructureItemAsync()
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        if (viewModel.IsBusy)
        {
            MessageBox.Show(
                this,
                "Wait for the current operation to finish before renaming an item.",
                "Archive Assist is busy",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var node = SelectedStructureNode();
        if (node is null ||
            (!File.Exists(node.FullPath) && !Directory.Exists(node.FullPath)))
        {
            MessageBox.Show(
                this,
                "Select an available file or folder first.",
                "Rename",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var dialog = new RenameItemWindow(node.Name, node.IsFolder)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (node.IsFolder)
        {
            var answer = MessageBox.Show(
                this,
                $"Rename the folder \"{node.Name}\" to \"{dialog.NewName}\"?\n\n" +
                "The paths of every file and subfolder inside it will change.",
                "Rename folder",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        var oldPath = node.FullPath;
        try
        {
            var newPath = FileSystemRenameService.Rename(oldPath, dialog.NewName);
            var refreshedSelection = viewModel.SelectedPaths
                .Select(path => RemapSelectionPath(
                    path,
                    oldPath,
                    newPath,
                    node.IsFolder))
                .ToList();
            await viewModel.ReplaceSelectionAsync(refreshedSelection);
            viewModel.SetStatus(
                $"Renamed {node.Name} to {Path.GetFileName(newPath)}. Discovery preview refreshed.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Could not rename item",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static string RemapSelectionPath(
        string selectionPath,
        string oldPath,
        string newPath,
        bool renamedFolder)
    {
        var fullSelectionPath = Path.GetFullPath(selectionPath);
        var fullOldPath = Path.GetFullPath(oldPath);
        if (string.Equals(
                fullSelectionPath,
                fullOldPath,
                StringComparison.OrdinalIgnoreCase))
        {
            return newPath;
        }

        if (!renamedFolder)
        {
            return fullSelectionPath;
        }

        var oldPrefix = fullOldPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        return fullSelectionPath.StartsWith(
            oldPrefix,
            StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(newPath, fullSelectionPath[oldPrefix.Length..])
            : fullSelectionPath;
    }

    private ReportRow? SelectedReportRow() => ReportGrid.SelectedItem as ReportRow;

    private FileStructureNode? SelectedStructureNode() => FileStructureTree.SelectedItem as FileStructureNode;

    private void ShowDetails(ReportRow? row)
    {
        if (row is null) return;
        new DetailsWindow(row, _metadataReader) { Owner = this }.ShowDialog();
    }

    private void OpenPdfEditor(string pdfPath)
    {
        if (RecentPdfService.Add(_settings, pdfPath))
        {
            _settingsService.Save(_settings);
        }

        var editor = new PdfEditorWindow(
            pdfPath,
            _settingsService,
            _settings,
            _recoveryService)
        {
            Owner = this
        };
        editor.PdfSaved += PdfEditor_PdfSaved;
        editor.RescanRequested += PdfEditor_RescanRequested;
        editor.Show();
    }

    private void PdfEditor_PdfSaved(object? sender, PdfSavedEventArgs e)
    {
        var message = e.WasBackupRestore
            ? $"{Path.GetFileName(e.PdfPath)} was restored from its original backup. " +
              "The edited version remains available in Recovery Center. " +
              "Run the scan again to refresh its report values."
            : e.WasSavedAsCopy
                ? $"{Path.GetFileName(e.PdfPath)} was saved as an edited copy and is now open in the editor."
            : $"{Path.GetFileName(e.PdfPath)} was edited and saved. " +
              "Run the scan again to refresh its report values.";
        ViewModel?.SetStatus(message);
    }

    private void PdfEditor_RescanRequested(object? sender, PdfRescanRequestedEventArgs e)
    {
        if (ViewModel is not { } viewModel || !viewModel.ScanCommand.CanExecute(null))
        {
            return;
        }

        e.ScanStarted = true;
        viewModel.ScanCommand.Execute(null);
    }

    private void SelectResultsTab(TabItem tab)
    {
        ResultsTabs.SelectedItem = tab;
        tab.Focus();
    }

    private void ViewModel_ScanCompleted(object? sender, ScanCompletedEventArgs e)
    {
        SelectResultsTab(ReportTab);
        ReportGrid.Focus();
        ShowToast(
            e.Summary.ErrorCount == 0
                ? "Scan complete. Review the Report or Summary."
                : $"Scan complete with {e.Summary.ErrorCount:N0} PDF error(s).");
    }

    private void ViewModel_SelectionChanged(object? sender, EventArgs e) =>
        SelectResultsTab(DiscoveryPreviewTab);

    private bool SetClipboardText(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Clipboard unavailable", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void ShowToast(string message)
    {
        ToastNotificationText.Text = message;
        ToastNotification.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void RunPathAction(Action action)
    {
        try { action(); }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Could not open path", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void SelectRowUnderPointer<TRow>(DependencyObject? source) where TRow : DataGridRow
    {
        var row = FindAncestor<TRow>(source);
        if (row is null) return;
        row.IsSelected = true;
        row.Focus();
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void RestoreWindowSettings(object? sender, RoutedEventArgs e)
    {
        if (RecentPdfService.PruneUnavailable(_settings))
        {
            _settingsService.Save(_settings);
        }
        if (RecentScanLocationService.PruneUnavailable(_settings))
        {
            _settingsService.Save(_settings);
        }

        if (double.IsFinite(_settings.WindowWidth) && _settings.WindowWidth >= MinWidth)
            Width = Math.Min(_settings.WindowWidth, SystemParameters.WorkArea.Width);
        if (double.IsFinite(_settings.WindowHeight) && _settings.WindowHeight >= MinHeight)
            Height = Math.Min(_settings.WindowHeight, SystemParameters.WorkArea.Height);

        if (_settings.WindowLeft is { } left && _settings.WindowTop is { } top &&
            double.IsFinite(left) && double.IsFinite(top))
        {
            var visibleDesktop = new Rect(
                SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth,
                SystemParameters.VirtualScreenHeight);
            var savedWindow = new Rect(left, top, Width, Height);
            if (visibleDesktop.IntersectsWith(savedWindow))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }
        }

        if (_settings.IsMaximized) WindowState = WindowState.Maximized;
        AdvancedSettingsExpander.IsExpanded =
            _settings.MainAdvancedSettingsExpanded;
        MainWelcomePanel.Visibility = _settings.HasSeenMainWelcome
            ? Visibility.Collapsed
            : Visibility.Visible;
        ApplyReportLayout();
    }

    private void SaveWindowSettings(object? sender, CancelEventArgs e)
    {
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;
        if (double.IsFinite(bounds.Width) && bounds.Width >= MinWidth)
            _settings.WindowWidth = bounds.Width;
        if (double.IsFinite(bounds.Height) && bounds.Height >= MinHeight)
            _settings.WindowHeight = bounds.Height;
        if (double.IsFinite(bounds.Left) && double.IsFinite(bounds.Top))
        {
            _settings.WindowLeft = bounds.Left;
            _settings.WindowTop = bounds.Top;
        }
        _settings.IsMaximized = WindowState == WindowState.Maximized;
        _settings.MainAdvancedSettingsExpanded =
            AdvancedSettingsExpander.IsExpanded;
        SaveReportLayout();
        _settingsService.Save(_settings);
    }

    private void ApplyReportLayout()
    {
        foreach (var column in ReportGrid.Columns)
        {
            var header = column.Header?.ToString() ?? string.Empty;
            if (_settings.ReportColumnWidths.TryGetValue(header, out var width) &&
                double.IsFinite(width) &&
                width >= 35)
            {
                column.Width = new DataGridLength(width);
            }
            else if (DefaultReportColumnWidth(header) is { } defaultWidth)
            {
                column.Width = new DataGridLength(defaultWidth);
            }

            column.SortDirection = null;
        }

        if (ViewModel is not { } viewModel)
        {
            return;
        }

        viewModel.ReportView.SortDescriptions.Clear();
        if (string.IsNullOrWhiteSpace(_settings.ReportSortProperty) ||
            !Enum.TryParse<ListSortDirection>(
                _settings.ReportSortDirection,
                ignoreCase: true,
                out var direction))
        {
            return;
        }

        var sortColumn = ReportGrid.Columns.FirstOrDefault(column =>
            string.Equals(
                GetColumnProperty(column),
                _settings.ReportSortProperty,
                StringComparison.Ordinal));
        if (sortColumn is null)
        {
            return;
        }

        viewModel.ReportView.SortDescriptions.Add(
            new SortDescription(_settings.ReportSortProperty, direction));
        sortColumn.SortDirection = direction;
    }

    private void SaveReportLayout()
    {
        _settings.ReportColumnWidths = ReportGrid.Columns
            .Select(column => new
            {
                Header = column.Header?.ToString() ?? string.Empty,
                Width = column.ActualWidth
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Header) &&
                           double.IsFinite(item.Width) &&
                           item.Width >= 35)
            .ToDictionary(
                item => item.Header,
                item => item.Width,
                StringComparer.Ordinal);

        SortDescription? sort =
            ViewModel is { } viewModel &&
            viewModel.ReportView.SortDescriptions.Count > 0
                ? viewModel.ReportView.SortDescriptions[0]
                : null;
        _settings.ReportSortProperty = sort?.PropertyName ?? string.Empty;
        _settings.ReportSortDirection = sort is null
            ? string.Empty
            : sort.Value.Direction.ToString();
    }

    private static string? GetColumnProperty(DataGridColumn column) =>
        (column as DataGridBoundColumn)?.Binding is Binding binding
            ? binding.Path?.Path
            : null;

    private static double? DefaultReportColumnWidth(string header) => header switch
    {
        "File Name" => 210,
        "Documents" => 90,
        "Maps" => 65,
        "Photos" => 70,
        "Photo Backs" => 95,
        "Total" => 65,
        "Type" => 90,
        "Folder" => 150,
        "Pages" => 65,
        "Searchable" => 90,
        "Non-OCR Pages" => 115,
        "OCR Check" => 185,
        "Largest Page" => 130,
        "Over Limit" => 80,
        "Size" => 85,
        "Issues" => 260,
        _ => null
    };

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.F:
                SelectResultsTab(ReportTab);
                ReportSearchBox.Focus();
                ReportSearchBox.SelectAll();
                e.Handled = true;
                break;
            case Key.D1:
                SelectResultsTab(DiscoveryPreviewTab);
                e.Handled = true;
                break;
            case Key.D2:
                SelectResultsTab(ReportTab);
                e.Handled = true;
                break;
            case Key.D3:
                SelectResultsTab(FileStructureTab);
                e.Handled = true;
                break;
        }
    }
}
