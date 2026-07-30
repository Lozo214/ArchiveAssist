using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ArchiveAssist.App.Controls;
using ArchiveAssist.App.Models;
using ArchiveAssist.App.Services;
using ArchiveAssist.Core.Models;
using ArchiveAssist.Core.Services;
using Microsoft.Win32;

namespace ArchiveAssist.App;

public partial class PdfEditorWindow : Window
{
    private const string PageDragDataFormat = "ArchiveAssist.PdfEditor.PageIds";
    private const int ThumbnailRenderWidth = 560;
    private const int DetailRenderWidth = 1800;
    private const int MaximumConcurrentRenders = 2;
    private const int MaximumCachedThumbnails = 80;
    private const int MaximumHistoryEntries = 20;
    private const long MaximumThumbnailCacheBytes = 96L * 1024 * 1024;
    private const long MaximumSnapshotBytes = 128L * 1024 * 1024;
    private const long MaximumHistoryBytes = 256L * 1024 * 1024;

    private readonly ObservableCollection<PdfPageThumbnail> _pages = [];
    private readonly PdfEditWorkspace _workspace;
    private readonly PdfPageRenderer _renderer = new();
    private readonly string? _initialPdfPath;
    private readonly ISettingsService? _settingsService;
    private readonly AppSettings? _settings;
    private readonly IFileRecoveryService _recoveryService;
    private readonly List<EditorHistoryEntry> _undoHistory = [];
    private readonly List<EditorHistoryEntry> _redoHistory = [];
    private readonly Dictionary<Guid, long> _thumbnailLastAccess = [];
    private readonly DispatcherTimer _thumbnailViewportTimer;
    private CancellationTokenSource? _thumbnailRenderCancellation;
    private CancellationTokenSource? _detailRenderCancellation;
    private long _thumbnailRenderGeneration;
    private long _detailRenderGeneration;
    private long _documentPreviewVersion;
    private long _thumbnailAccessClock;
    private long _detailRenderedVersion = -1;
    private Guid? _activePageId;
    private Guid? _detailRenderedPageId;
    private EditorViewMode _viewMode = EditorViewMode.ThumbnailGrid;
    private DetailFitMode _detailFitMode = DetailFitMode.FitPage;
    private bool _hasUnsavedChanges;
    private bool _isOperationBusy;
    private bool _isRenderingThumbnails;
    private bool _isSynchronizingSelection;
    private bool _isUpdatingDetailZoom;
    private bool _isPanning;
    private Point? _pageDragStart;
    private PdfPageThumbnail? _pageDragOrigin;
    private ListBoxItem? _pageDropTarget;
    private bool _dropAfterTarget;
    private bool _dropTargetIsVertical;
    private Point _panStartPoint;
    private double _panStartHorizontalOffset;
    private double _panStartVerticalOffset;

    public PdfEditorWindow(
        string? initialPdfPath = null,
        ISettingsService? settingsService = null,
        AppSettings? settings = null,
        IFileRecoveryService? recoveryService = null)
    {
        _initialPdfPath = initialPdfPath;
        _settingsService = settingsService;
        _settings = settings;
        _recoveryService = recoveryService ?? FileRecoveryService.CreateDefault();
        _workspace = new PdfEditWorkspace(
            _recoveryService,
            settings?.RecoveryRetentionDays ?? 30);
        InitializeComponent();
        PageThumbnailList.ItemsSource = _pages;
        PageNavigationList.ItemsSource = _pages;
        _thumbnailViewportTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(120),
            DispatcherPriority.Background,
            ThumbnailViewportTimer_Tick,
            Dispatcher);
        _thumbnailViewportTimer.Stop();
        Loaded += PdfEditorWindow_Loaded;
    }

    public event EventHandler<PdfSavedEventArgs>? PdfSaved;

    public event EventHandler<PdfRescanRequestedEventArgs>? RescanRequested;

    private PdfPageThumbnail? ActivePage =>
        _activePageId is { } id
            ? _pages.FirstOrDefault(page => page.Id == id)
            : null;

    private async void PdfEditorWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RestoreEditorSettings();
        EditorWelcomePanel.Visibility = _settings is { HasSeenEditorWelcome: false }
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(_initialPdfPath))
        {
            await OpenPdfAsync(_initialPdfPath);
        }
    }

    private async void OpenPdfMenuItem_Click(object sender, RoutedEventArgs e) =>
        await SelectAndOpenPdfAsync();

    private void SaveMenuItem_Click(object sender, RoutedEventArgs e) => SaveChanges();

    private void SaveCopyMenuItem_Click(object sender, RoutedEventArgs e) =>
        SaveChangesAsCopy();

    private void CloseMenuItem_Click(object sender, RoutedEventArgs e) => Close();

    private async void RotateLeft_Click(object sender, RoutedEventArgs e) =>
        await RotateSelectedPagesAsync(-90);

    private async void RotateRight_Click(object sender, RoutedEventArgs e) =>
        await RotateSelectedPagesAsync(90);

    private async void Delete_Click(object sender, RoutedEventArgs e) =>
        await DeleteSelectedPagesAsync();

    private async void Crop_Click(object sender, RoutedEventArgs e) =>
        await CropSelectedPagesAsync();

    private async void Undo_Click(object sender, RoutedEventArgs e) =>
        await RestoreHistoryAsync(_undoHistory, _redoHistory, "Undoing");

    private async void Redo_Click(object sender, RoutedEventArgs e) =>
        await RestoreHistoryAsync(_redoHistory, _undoHistory, "Redoing");

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        SetEditorView(EditorViewMode.ThumbnailGrid);
        SelectPageIds(_pages.Select(page => page.Id), _activePageId);
        StatusText.Text = $"Selected all {_pages.Count:N0} page(s).";
    }

    private void GridViewToggle_Click(object sender, RoutedEventArgs e) =>
        SetEditorView(EditorViewMode.ThumbnailGrid);

    private async void PageViewToggle_Click(object sender, RoutedEventArgs e)
    {
        SetEditorView(EditorViewMode.Page);
        await RenderActiveDetailPageAsync();
    }

    private async void FitPage_Click(object sender, RoutedEventArgs e)
    {
        SetEditorView(EditorViewMode.Page);
        _detailFitMode = DetailFitMode.FitPage;
        await RenderActiveDetailPageAsync();
        ApplyDetailFit();
    }

    private async void FitWidth_Click(object sender, RoutedEventArgs e)
    {
        SetEditorView(EditorViewMode.Page);
        _detailFitMode = DetailFitMode.FitWidth;
        await RenderActiveDetailPageAsync();
        ApplyDetailFit();
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => AdjustCurrentViewZoom(zoomIn: true);

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => AdjustCurrentViewZoom(zoomIn: false);

    private async void PreviousPage_Click(object sender, RoutedEventArgs e) =>
        await NavigatePageAsync(-1);

    private async void NextPage_Click(object sender, RoutedEventArgs e) =>
        await NavigatePageAsync(1);

    private async void OpenRecoveryCenter_Click(object sender, RoutedEventArgs e)
    {
        if (_hasUnsavedChanges)
        {
            StatusText.Text = "Save or discard the current edits before opening Recovery Center.";
            return;
        }

        var sourcePath = _workspace.SourcePath;
        var recoveryCenter = new RecoveryCenterWindow(
            _recoveryService,
            _settings?.RecoveryRetentionDays ?? 30,
            sourcePath)
        {
            Owner = this
        };
        recoveryCenter.ShowDialog();
        if (sourcePath is not null &&
            recoveryCenter.RestoredPaths.Contains(sourcePath) &&
            File.Exists(sourcePath))
        {
            await OpenPdfAsync(sourcePath);
            StatusText.Text = $"Reloaded {Path.GetFileName(sourcePath)} after restoring it.";
        }
    }

    private async void RestoreBackup_Click(object sender, RoutedEventArgs e) =>
        await RestoreSessionBackupAsync();

    private void RecentPdfsMenuItem_SubmenuOpened(object sender, RoutedEventArgs e) =>
        PopulateRecentPdfMenu();

    private async void RecentPdfMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string pdfPath } || !File.Exists(pdfPath))
        {
            PopulateRecentPdfMenu();
            StatusText.Text = "That recent PDF is no longer available.";
            return;
        }

        if (_isOperationBusy)
        {
            StatusText.Text = "Wait for the current PDF operation to finish before opening another file.";
            return;
        }

        if (!ConfirmSaveOrDiscardUnsavedChanges("open another PDF"))
        {
            return;
        }

        await OpenPdfAsync(pdfPath);
    }

    private void ShowGettingStarted_Click(object sender, RoutedEventArgs e) =>
        EditorWelcomePanel.Visibility = Visibility.Visible;

    private void DismissGettingStarted_Click(object sender, RoutedEventArgs e)
    {
        EditorWelcomePanel.Visibility = Visibility.Collapsed;
        if (_settings is not { } settings || _settingsService is null)
        {
            return;
        }

        settings.HasSeenEditorWelcome = true;
        _settingsService.Save(settings);
    }

    private void PopulateRecentPdfMenu()
    {
        RecentPdfsMenuItem.Items.Clear();
        if (_settings is not { } settings)
        {
            RecentPdfsMenuItem.Items.Add(new MenuItem
            {
                Header = "(No recent PDFs)",
                IsEnabled = false
            });
            return;
        }

        if (RecentPdfService.PruneUnavailable(settings))
        {
            _settingsService?.Save(settings);
        }

        var recentPaths = RecentPdfService.Available(settings);
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

    private async Task RestoreSessionBackupAsync()
    {
        if (_isOperationBusy)
        {
            StatusText.Text = "Wait for the current PDF operation to finish before restoring a backup.";
            return;
        }

        var backupPath = _workspace.SessionBackupPath;
        if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
        {
            StatusText.Text = "No editing-session recovery point is available to restore.";
            return;
        }

        var response = MessageBox.Show(
            this,
            "Restore the original PDF from this editing session's recovery point?\n\n" +
            "Archive Assist will preserve the currently saved edited PDF as another managed recovery point first. " +
            "Any edits that have not been saved will be discarded.",
            "Restore original PDF",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (response != MessageBoxResult.Yes)
        {
            return;
        }

        PdfBackupRestoreResult? result = null;
        var restored = false;
        try
        {
            BeginOperation("Restoring the original PDF...");
            CancelAllRendering();
            result = await Task.Run(_workspace.RestoreSessionBackup);
            ClearHistory();
            _hasUnsavedChanges = false;
            _documentPreviewVersion++;
            _detailRenderedPageId = null;
            _detailRenderedVersion = -1;
            DetailPageImage.Source = null;
            SynchronizePageModels(_workspace.Pages.Select(page => page.Id).ToHashSet());
            SelectPageIndexes([0]);
            UpdateDocumentHeading();
            restored = true;

            var sourcePath = _workspace.SourcePath
                ?? throw new InvalidOperationException("The restored PDF path is unavailable.");
            StatusText.Text =
                $"Restored {Path.GetFileName(sourcePath)}. The edited version remains available in Recovery Center.";
            PdfSaved?.Invoke(
                this,
                new PdfSavedEventArgs(
                    sourcePath,
                    result.RestoredBackupPath,
                    wasBackupRestore: true,
                    preservedEditedPath: result.PreservedEditedPath));
        }
        catch (Exception exception)
        {
            ShowEditorError("restore the original PDF", exception);
        }
        finally
        {
            EndOperation();
        }

        if (restored)
        {
            await RefreshPreviewsAsync(
                _activePageId is { } activeId ? new HashSet<Guid> { activeId } : null);
        }
    }

    private void RescanReport_Click(object sender, RoutedEventArgs e)
    {
        if (_hasUnsavedChanges)
        {
            StatusText.Text = "Save the current changes before rescanning.";
            return;
        }

        var args = new PdfRescanRequestedEventArgs();
        RescanRequested?.Invoke(this, args);
        if (!args.ScanStarted)
        {
            StatusText.Text =
                "There is no current scan to rerun. Return to Archive Assist and select files or folders first.";
            return;
        }

        Close();
    }

    private void KeyboardShortcuts_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(
            this,
            "Ctrl+O   Open PDF\n" +
            "Ctrl+S   Save\n" +
            "Ctrl+Z   Undo\n" +
            "Ctrl+Y   Redo\n" +
            "Ctrl+A   Select all pages\n" +
            "Ctrl+1   Thumbnail Grid\n" +
            "Ctrl+2   Page View\n" +
            "Ctrl+0   Fit Page\n" +
            "Ctrl+Shift+0   Fit Width\n" +
            "Ctrl++ / Ctrl+-   Zoom\n" +
            "Page Up / Page Down   Previous or next page\n" +
            "Delete   Delete selected pages\n" +
            "Drag selected pages   Reorder pages",
            "PDF Editor keyboard shortcuts",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

    private async Task DeleteSelectedPagesAsync()
    {
        var selectedPages = GetSelectedPages();
        if (selectedPages.Count == 0)
        {
            return;
        }

        if (selectedPages.Count >= _pages.Count)
        {
            MessageBox.Show(
                this,
                "A PDF must keep at least one page.",
                "Cannot delete every page",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var response = MessageBox.Show(
            this,
            $"Delete {selectedPages.Count:N0} selected page(s)?\n\n" +
            "The change remains undoable until you save or open another PDF.",
            "Delete selected pages",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (response != MessageBoxResult.Yes)
        {
            return;
        }

        var selectedIndexes = selectedPages.Select(page => page.PageIndex).ToList();
        var nextSelectionIndex = selectedIndexes.Min();
        EditorHistoryEntry? historyEntry = null;
        var editCompleted = false;

        try
        {
            BeginOperation("Deleting selected pages...");
            CancelAllRendering();
            historyEntry = await CaptureHistoryEntryAsync($"Delete {selectedPages.Count:N0} page(s)");
            await Task.Run(() => _workspace.DeletePages(selectedIndexes));
            editCompleted = true;
            RecordCompletedEdit(historyEntry);
            MarkDocumentChanged();
            SynchronizePageModels();
            SelectPageIndexes([Math.Min(nextSelectionIndex, _pages.Count - 1)]);
            StatusText.Text = BuildEditCompletedStatus(
                $"Deleted {selectedIndexes.Count:N0} page(s).",
                historyEntry is not null);
        }
        catch (Exception exception)
        {
            ShowEditorError("delete the selected pages", exception);
        }
        finally
        {
            EndOperation();
        }

        if (editCompleted)
        {
            await RefreshPreviewsAsync();
        }
    }

    private async Task CropSelectedPagesAsync()
    {
        var selectedPages = GetSelectedPages();
        if (selectedPages.Count == 0)
        {
            return;
        }

        var selectedIndexes = selectedPages.Select(page => page.PageIndex).ToList();
        var selectedIds = selectedPages.Select(page => page.Id).ToHashSet();
        EditorHistoryEntry? historyEntry = null;
        var cropApplied = false;

        try
        {
            BeginOperation("Preparing the crop preview...");
            var previewPageIndex = selectedIndexes[0];
            var pageSize = _workspace.GetPageSize(previewPageIndex);
            var pdfBytes = _workspace.GetPdfBytesSnapshot();
            var previewImage = await Task.Run(
                () => _renderer.RenderPage(pdfBytes, previewPageIndex, widthPixels: 1400));

            var dialog = new CropPagesWindow(previewImage, pageSize.Width, pageSize.Height)
            {
                Owner = this
            };

            if (dialog.ShowDialog() != true)
            {
                StatusText.Text = $"{selectedPages.Count:N0} page(s) selected.";
                return;
            }

            CancelAllRendering();
            StatusText.Text = "Applying the crop...";
            historyEntry = await CaptureHistoryEntryAsync($"Crop {selectedPages.Count:N0} page(s)");
            await Task.Run(() => _workspace.CropPages(
                selectedIndexes,
                dialog.LeftCrop,
                dialog.TopCrop,
                dialog.RightCrop,
                dialog.BottomCrop));

            cropApplied = true;
            RecordCompletedEdit(historyEntry);
            MarkDocumentChanged();
            SynchronizePageModels(selectedIds);
            SelectPageIds(selectedIds, selectedPages[0].Id);
            StatusText.Text = BuildEditCompletedStatus(
                $"Cropped {selectedPages.Count:N0} page(s).",
                historyEntry is not null);
        }
        catch (Exception exception)
        {
            ShowEditorError("crop the selected pages", exception);
        }
        finally
        {
            EndOperation();
        }

        if (cropApplied)
        {
            await RefreshPreviewsAsync(selectedIds);
        }
    }

    private async Task RotateSelectedPagesAsync(int degrees)
    {
        var selectedPages = GetSelectedPages();
        if (selectedPages.Count == 0)
        {
            return;
        }

        var selectedIndexes = selectedPages.Select(page => page.PageIndex).ToList();
        var selectedIds = selectedPages.Select(page => page.Id).ToHashSet();
        EditorHistoryEntry? historyEntry = null;
        var rotated = false;

        try
        {
            BeginOperation("Rotating selected pages...");
            CancelAllRendering();
            var direction = degrees < 0 ? "left" : "right";
            historyEntry = await CaptureHistoryEntryAsync(
                $"Rotate {selectedPages.Count:N0} page(s) {direction}");
            await Task.Run(() => _workspace.RotatePages(selectedIndexes, degrees));

            rotated = true;
            RecordCompletedEdit(historyEntry);
            MarkDocumentChanged();
            SynchronizePageModels(selectedIds);
            SelectPageIds(selectedIds, selectedPages[0].Id);
            StatusText.Text = BuildEditCompletedStatus(
                $"Rotated {selectedPages.Count:N0} page(s).",
                historyEntry is not null);
        }
        catch (Exception exception)
        {
            ShowEditorError("rotate the selected pages", exception);
        }
        finally
        {
            EndOperation();
        }

        if (rotated)
        {
            await RefreshPreviewsAsync(selectedIds);
        }
    }

    private async Task SelectAndOpenPdfAsync()
    {
        if (_isOperationBusy)
        {
            StatusText.Text = "Wait for the current PDF operation to finish before opening another file.";
            return;
        }

        if (!ConfirmSaveOrDiscardUnsavedChanges("open another PDF"))
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Open PDF in Archive Assist",
            Filter = "PDF files (*.pdf)|*.pdf|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            await OpenPdfAsync(dialog.FileName);
        }
    }

    private async Task OpenPdfAsync(string pdfPath)
    {
        var opened = false;

        try
        {
            BeginOperation($"Opening {Path.GetFileName(pdfPath)}...");
            CancelAllRendering();
            await Task.Run(() => _workspace.Open(pdfPath));
            _thumbnailLastAccess.Clear();
            _thumbnailAccessClock = 0;
            ClearHistory();
            _hasUnsavedChanges = false;
            _documentPreviewVersion++;
            _detailRenderedPageId = null;
            _detailRenderedVersion = -1;
            DetailPageImage.Source = null;
            SynchronizePageModels();
            ApplyThumbnailZoom(ThumbnailZoomSlider.Value);
            SelectPageIndexes([0]);
            UpdateDocumentHeading();
            if (_settings is { } settings &&
                RecentPdfService.Add(settings, pdfPath))
            {
                _settingsService?.Save(settings);
            }
            opened = true;
        }
        catch (Exception exception)
        {
            ShowEditorError("open that PDF", exception);
        }
        finally
        {
            EndOperation();
        }

        if (opened)
        {
            await RefreshPreviewsAsync(
                _activePageId is { } activeId ? new HashSet<Guid> { activeId } : null);
        }
    }

    private bool SaveChanges()
    {
        if (_isOperationBusy)
        {
            StatusText.Text = "Wait for the current PDF operation to finish before saving.";
            return false;
        }

        if (!_workspace.HasDocument || !_hasUnsavedChanges)
        {
            return true;
        }

        var safetyMode = FileSafetyModes.Normalize(_settings?.FileSafetyMode);
        if (safetyMode == FileSafetyModes.SaveCopies)
        {
            return SaveChangesAsCopy();
        }

        if (safetyMode == FileSafetyModes.AlwaysAsk)
        {
            var choice = MessageBox.Show(
                this,
                "How should Archive Assist save these edits?\n\n" +
                "Yes — update the open PDF and keep a managed recovery point.\n" +
                "No — save an edited copy and continue working in that copy.\n" +
                "Cancel — return to the editor without saving.",
                "Choose how to save",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);
            if (choice == MessageBoxResult.No)
            {
                return SaveChangesAsCopy();
            }

            if (choice != MessageBoxResult.Yes)
            {
                return false;
            }
        }

        return SaveChangesInPlace();
    }

    private bool SaveChangesInPlace()
    {
        try
        {
            var sourcePath = _workspace.SourcePath
                ?? throw new InvalidOperationException("The editor does not know which PDF was opened.");
            var backupPath = _workspace.Save();
            _hasUnsavedChanges = false;
            ClearHistory();
            UpdateDocumentHeading();
            var retentionText = (_settings?.RecoveryRetentionDays ?? 30) > 0
                ? $"for {(_settings?.RecoveryRetentionDays ?? 30):N0} days"
                : "until you delete it";
            StatusText.Text =
                $"Saved {Path.GetFileName(sourcePath)} in place. Its original version is available in Recovery Center {retentionText}.";
            PdfSaved?.Invoke(this, new PdfSavedEventArgs(sourcePath, backupPath));
            return true;
        }
        catch (Exception exception)
        {
            ShowEditorError("save the PDF", exception);
            return false;
        }
    }

    private bool SaveChangesAsCopy()
    {
        if (!_workspace.HasDocument)
        {
            return false;
        }

        var sourcePath = _workspace.SourcePath
            ?? throw new InvalidOperationException("The editor does not know which PDF was opened.");
        var dialog = new SaveFileDialog
        {
            Title = "Save Edited PDF Copy",
            Filter = "PDF files (*.pdf)|*.pdf",
            DefaultExt = ".pdf",
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = Path.GetDirectoryName(sourcePath),
            FileName = $"{Path.GetFileNameWithoutExtension(sourcePath)}.edited.pdf"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return false;
        }

        try
        {
            _workspace.SaveCopyAndContinue(dialog.FileName);
            _hasUnsavedChanges = false;
            ClearHistory();
            UpdateDocumentHeading();
            if (_settings is { } settings &&
                RecentPdfService.Add(settings, dialog.FileName))
            {
                _settingsService?.Save(settings);
            }

            StatusText.Text =
                $"Saved and opened {Path.GetFileName(dialog.FileName)}. The previous PDF was not changed.";
            PdfSaved?.Invoke(
                this,
                new PdfSavedEventArgs(
                    dialog.FileName,
                    string.Empty,
                    wasSavedAsCopy: true));
            return true;
        }
        catch (Exception exception)
        {
            ShowEditorError("save an edited copy", exception);
            return false;
        }
    }

    private bool ConfirmSaveOrDiscardUnsavedChanges(string actionDescription)
    {
        if (!_hasUnsavedChanges)
        {
            return true;
        }

        var response = MessageBox.Show(
            this,
            $"This editor has unsaved changes. Save them before you {actionDescription}?",
            "Unsaved PDF changes",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        return response switch
        {
            MessageBoxResult.Yes => SaveChanges(),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    private async Task<EditorHistoryEntry?> CaptureHistoryEntryAsync(string description)
    {
        if (_workspace.PdfByteLength > MaximumSnapshotBytes)
        {
            return null;
        }

        var selectedIds = GetSelectedPages().Select(page => page.Id).ToList();
        var activePageId = _activePageId;
        var wasDirty = _hasUnsavedChanges;
        var snapshot = await Task.Run(_workspace.CreateSnapshot);
        return new(description, snapshot, wasDirty, selectedIds, activePageId);
    }

    private void RecordCompletedEdit(EditorHistoryEntry? historyEntry)
    {
        _redoHistory.Clear();

        if (historyEntry is null)
        {
            _undoHistory.Clear();
            UpdateControls();
            return;
        }

        _undoHistory.Add(historyEntry);
        TrimHistory();
        UpdateControls();
    }

    private async Task RestoreHistoryAsync(
        List<EditorHistoryEntry> source,
        List<EditorHistoryEntry> destination,
        string statusVerb)
    {
        if (_isOperationBusy || source.Count == 0)
        {
            return;
        }

        var target = source[^1];
        var restored = false;

        try
        {
            BeginOperation($"{statusVerb} {target.Description.ToLowerInvariant()}...");
            CancelAllRendering();
            var currentState = await CaptureHistoryEntryAsync(target.Description)
                ?? throw new InvalidOperationException(
                    "Archive Assist could not capture the current PDF state for undo.");

            source.RemoveAt(source.Count - 1);
            destination.Add(currentState);
            await Task.Run(() => _workspace.RestoreSnapshot(target.Snapshot));
            _hasUnsavedChanges = target.WasDirty;
            _documentPreviewVersion++;
            _detailRenderedPageId = null;
            _detailRenderedVersion = -1;
            var allPageIds = _workspace.Pages.Select(page => page.Id).ToHashSet();
            SynchronizePageModels(allPageIds);
            SelectPageIds(target.SelectedPageIds, target.ActivePageId);
            TrimHistory();
            UpdateDocumentHeading();
            restored = true;
            StatusText.Text = $"{statusVerb.Replace("ing", string.Empty)} complete: {target.Description}.";
        }
        catch (Exception exception)
        {
            var action = statusVerb.StartsWith("Undo", StringComparison.Ordinal)
                ? "undo the last edit"
                : "redo the last edit";
            ShowEditorError(action, exception);
        }
        finally
        {
            EndOperation();
        }

        if (restored)
        {
            await RefreshPreviewsAsync();
        }
    }

    private void TrimHistory()
    {
        while (_undoHistory.Count + _redoHistory.Count > MaximumHistoryEntries ||
               TotalHistoryBytes() > MaximumHistoryBytes)
        {
            if (_undoHistory.Count > 1)
            {
                _undoHistory.RemoveAt(0);
            }
            else if (_redoHistory.Count > 1)
            {
                _redoHistory.RemoveAt(0);
            }
            else
            {
                break;
            }
        }
    }

    private long TotalHistoryBytes() =>
        _undoHistory.Sum(entry => entry.Snapshot.ByteLength) +
        _redoHistory.Sum(entry => entry.Snapshot.ByteLength);

    private void ClearHistory()
    {
        _undoHistory.Clear();
        _redoHistory.Clear();
        UpdateControls();
    }

    private static string BuildEditCompletedStatus(string message, bool historyRecorded) =>
        historyRecorded
            ? $"{message} Press Ctrl+S to save or Ctrl+Z to undo."
            : $"{message} Press Ctrl+S to save. Undo was limited because this PDF is very large.";

    private void MarkDocumentChanged()
    {
        _hasUnsavedChanges = true;
        _documentPreviewVersion++;
        _detailRenderedPageId = null;
        _detailRenderedVersion = -1;
        UpdateDocumentHeading();
    }

    private void SynchronizePageModels(ISet<Guid>? invalidatedPageIds = null)
    {
        var existingById = _pages.ToDictionary(page => page.Id);
        var orderedPages = new List<PdfPageThumbnail>(_workspace.Pages.Count);

        foreach (var page in _workspace.Pages)
        {
            if (!existingById.TryGetValue(page.Id, out var thumbnail))
            {
                thumbnail = new PdfPageThumbnail(page);
            }
            else
            {
                thumbnail.UpdateFrom(page);
            }

            if (invalidatedPageIds?.Contains(page.Id) == true)
            {
                thumbnail.InvalidateThumbnail();
                _thumbnailLastAccess.Remove(page.Id);
            }

            thumbnail.ThumbnailLongEdge = ThumbnailZoomSlider.Value;
            orderedPages.Add(thumbnail);
        }

        _isSynchronizingSelection = true;
        try
        {
            _pages.Clear();
            foreach (var page in orderedPages)
            {
                _pages.Add(page);
            }
        }
        finally
        {
            _isSynchronizingSelection = false;
        }

        var currentPageIds = orderedPages.Select(page => page.Id).ToHashSet();
        foreach (var staleId in _thumbnailLastAccess.Keys
                     .Where(id => !currentPageIds.Contains(id))
                     .ToList())
        {
            _thumbnailLastAccess.Remove(staleId);
        }

        EmptyStateText.Visibility = _pages.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdatePagePosition();
        UpdateControls();
    }

    private async Task RefreshPreviewsAsync(ISet<Guid>? priorityPageIds = null)
    {
        var detailTask = _viewMode == EditorViewMode.Page
            ? RenderActiveDetailPageAsync(force: true)
            : Task.CompletedTask;
        var thumbnailTask = RenderPendingThumbnailsAsync(priorityPageIds);
        await Task.WhenAll(detailTask, thumbnailTask);
    }

    private async Task RenderPendingThumbnailsAsync(ISet<Guid>? priorityPageIds = null)
    {
        CancelThumbnailRendering();
        if (!_workspace.HasDocument)
        {
            return;
        }

        await Dispatcher.InvokeAsync(
            () => { },
            DispatcherPriority.Loaded);
        var desiredPages = GetDesiredThumbnailPages(priorityPageIds);
        var protectedIds = desiredPages.Select(page => page.Id).ToHashSet();
        foreach (var page in desiredPages.Where(page => page.ThumbnailImage is not null))
        {
            TouchThumbnail(page);
        }

        var targets = desiredPages
            .Where(page => page.ThumbnailImage is null)
            .ToList();

        if (targets.Count == 0)
        {
            TrimThumbnailCache(protectedIds);
            return;
        }

        var generation = Interlocked.Increment(ref _thumbnailRenderGeneration);
        var cancellation = new CancellationTokenSource();
        _thumbnailRenderCancellation = cancellation;
        var token = cancellation.Token;
        var pdfBytes = _workspace.GetPdfBytesSnapshot();
        var renderWidth = Math.Clamp(
            (int)Math.Ceiling(ThumbnailZoomSlider.Value * 1.5),
            240,
            ThumbnailRenderWidth);
        var completed = 0;
        var failed = 0;
        _isRenderingThumbnails = true;
        RenderProgressBar.Minimum = 0;
        RenderProgressBar.Maximum = targets.Count;
        RenderProgressBar.Value = 0;
        RenderProgressBar.Visibility = Visibility.Visible;
        StatusText.Text = $"Rendering {targets.Count:N0} page thumbnail(s)...";

        using var renderSlots = new SemaphoreSlim(MaximumConcurrentRenders);

        try
        {
            var renderTasks = targets.Select(async page =>
            {
                await renderSlots.WaitAsync(token);
                try
                {
                    token.ThrowIfCancellationRequested();
                    var image = await Task.Run(
                        () => _renderer.RenderPage(
                            pdfBytes,
                            page.PageIndex,
                            renderWidth),
                        token);
                    token.ThrowIfCancellationRequested();

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (generation != Volatile.Read(ref _thumbnailRenderGeneration) ||
                            token.IsCancellationRequested)
                        {
                            return;
                        }

                        page.ThumbnailImage = image;
                        page.RenderError = null;
                        TouchThumbnail(page);
                        RenderProgressBar.Value = ++completed;
                    });
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
                    Interlocked.Increment(ref failed);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (generation != Volatile.Read(ref _thumbnailRenderGeneration) ||
                            token.IsCancellationRequested)
                        {
                            return;
                        }

                        page.ThumbnailImage = null;
                        page.RenderError = FriendlyRenderError(exception);
                        RenderProgressBar.Value = ++completed;
                    });
                }
                finally
                {
                    renderSlots.Release();
                }
            });

            await Task.WhenAll(renderTasks);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_thumbnailRenderCancellation, cancellation))
            {
                _thumbnailRenderCancellation = null;
            }

            cancellation.Dispose();
        }

        if (generation != Volatile.Read(ref _thumbnailRenderGeneration))
        {
            return;
        }

        _isRenderingThumbnails = false;
        RenderProgressBar.Visibility = Visibility.Collapsed;
        TrimThumbnailCache(protectedIds);
        StatusText.Text = failed == 0
            ? BuildReadyStatus()
            : $"Ready, but {failed:N0} page thumbnail(s) could not be rendered. Editing is still available.";
    }

    private async Task RenderActiveDetailPageAsync(bool force = false)
    {
        var page = ActivePage;
        if (!_workspace.HasDocument || page is null)
        {
            ClearDetailPreview();
            return;
        }

        if (!force &&
            _detailRenderedPageId == page.Id &&
            _detailRenderedVersion == _documentPreviewVersion &&
            DetailPageImage.Source is not null)
        {
            UpdatePagePosition();
            return;
        }

        CancelDetailRendering();
        var generation = Interlocked.Increment(ref _detailRenderGeneration);
        var cancellation = new CancellationTokenSource();
        _detailRenderCancellation = cancellation;
        var token = cancellation.Token;
        var pdfBytes = _workspace.GetPdfBytesSnapshot();
        var pageId = page.Id;
        var pageIndex = page.PageIndex;
        var previewVersion = _documentPreviewVersion;

        if (page.ThumbnailImage is BitmapSource thumbnail)
        {
            SetDetailImage(thumbnail);
        }

        DetailEmptyText.Text = $"Rendering page {page.PageNumber:N0}...";
        StatusText.Text = $"Rendering detailed preview for page {page.PageNumber:N0}...";

        try
        {
            var image = await Task.Run(
                () => _renderer.RenderPage(pdfBytes, pageIndex, DetailRenderWidth),
                token);
            token.ThrowIfCancellationRequested();

            if (generation != Volatile.Read(ref _detailRenderGeneration) ||
                pageId != _activePageId ||
                previewVersion != _documentPreviewVersion)
            {
                return;
            }

            SetDetailImage(image);
            _detailRenderedPageId = pageId;
            _detailRenderedVersion = previewVersion;
            ApplyDetailFit();
            StatusText.Text = BuildReadyStatus();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (generation == Volatile.Read(ref _detailRenderGeneration))
            {
                DetailPageImage.Source = null;
                DetailPageBorder.Visibility = Visibility.Collapsed;
                DetailEmptyText.Visibility = Visibility.Visible;
                DetailEmptyText.Text = $"Page preview unavailable.\n{FriendlyRenderError(exception)}";
                StatusText.Text = "The detailed preview could not be rendered, but editing is still available.";
            }
        }
        finally
        {
            if (ReferenceEquals(_detailRenderCancellation, cancellation))
            {
                _detailRenderCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void SetDetailImage(BitmapSource image)
    {
        DetailPageImage.Source = image;
        DetailPageBorder.Visibility = Visibility.Visible;
        DetailEmptyText.Visibility = Visibility.Collapsed;
        ApplyDetailZoom(DetailZoomSlider.Value, preserveViewportCenter: false);
    }

    private void ClearDetailPreview()
    {
        CancelDetailRendering();
        DetailPageImage.Source = null;
        DetailPageBorder.Visibility = Visibility.Collapsed;
        DetailEmptyText.Visibility = Visibility.Visible;
        DetailEmptyText.Text = "Select a page to inspect it.";
        _detailRenderedPageId = null;
        _detailRenderedVersion = -1;
        UpdatePagePosition();
    }

    private static string FriendlyRenderError(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return message.Length <= 70 ? message : $"{message[..67]}...";
    }

    private void CancelAllRendering()
    {
        CancelThumbnailRendering();
        CancelDetailRendering();
    }

    private void CancelThumbnailRendering()
    {
        Interlocked.Increment(ref _thumbnailRenderGeneration);
        _thumbnailRenderCancellation?.Cancel();
        _isRenderingThumbnails = false;
        RenderProgressBar.Visibility = Visibility.Collapsed;
    }

    private IReadOnlyList<PdfPageThumbnail> GetDesiredThumbnailPages(
        ISet<Guid>? priorityPageIds)
    {
        var desiredIds = new HashSet<Guid>();
        AddRealizedPageIds(PageThumbnailList, desiredIds);
        AddRealizedPageIds(PageNavigationList, desiredIds);
        if (priorityPageIds is not null)
        {
            desiredIds.UnionWith(priorityPageIds);
        }

        if (_activePageId is { } activePageId)
        {
            desiredIds.Add(activePageId);
        }

        if (desiredIds.Count == 0)
        {
            desiredIds.UnionWith(_pages.Take(16).Select(page => page.Id));
        }

        return _pages
            .Where(page => desiredIds.Contains(page.Id))
            .OrderByDescending(page => priorityPageIds?.Contains(page.Id) == true)
            .ThenBy(page => page.PageIndex)
            .ToList();
    }

    private static void AddRealizedPageIds(
        ListBox listBox,
        ISet<Guid> target)
    {
        foreach (var item in listBox.Items)
        {
            if (item is PdfPageThumbnail page &&
                listBox.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem
                {
                    IsVisible: true
                })
            {
                target.Add(page.Id);
            }
        }
    }

    private void TouchThumbnail(PdfPageThumbnail page) =>
        _thumbnailLastAccess[page.Id] = ++_thumbnailAccessClock;

    private void TrimThumbnailCache(ISet<Guid> protectedIds)
    {
        var cachedPages = _pages
            .Where(page => page.ThumbnailImage is BitmapSource)
            .ToList();
        var cacheBytes = cachedPages.Sum(EstimateThumbnailBytes);
        if (cachedPages.Count <= MaximumCachedThumbnails &&
            cacheBytes <= MaximumThumbnailCacheBytes)
        {
            return;
        }

        foreach (var page in cachedPages
                     .Where(page => !protectedIds.Contains(page.Id))
                     .OrderBy(page => _thumbnailLastAccess.GetValueOrDefault(page.Id)))
        {
            if (cachedPages.Count <= MaximumCachedThumbnails &&
                cacheBytes <= MaximumThumbnailCacheBytes)
            {
                break;
            }

            cacheBytes -= EstimateThumbnailBytes(page);
            cachedPages.Remove(page);
            page.ThumbnailImage = null;
            _thumbnailLastAccess.Remove(page.Id);
        }
    }

    private static long EstimateThumbnailBytes(PdfPageThumbnail page) =>
        page.ThumbnailImage is BitmapSource bitmap
            ? (long)bitmap.PixelWidth * bitmap.PixelHeight *
              Math.Max(1, (bitmap.Format.BitsPerPixel + 7) / 8)
            : 0;

    private void PageList_ScrollChanged(object sender, ScrollChangedEventArgs e) =>
        ScheduleViewportThumbnailRender();

    private void ScheduleViewportThumbnailRender()
    {
        if (!_workspace.HasDocument || _isOperationBusy)
        {
            return;
        }

        _thumbnailViewportTimer.Stop();
        _thumbnailViewportTimer.Start();
    }

    private async void ThumbnailViewportTimer_Tick(object? sender, EventArgs e)
    {
        _thumbnailViewportTimer.Stop();
        if (_isOperationBusy || !_workspace.HasDocument)
        {
            return;
        }

        await RenderPendingThumbnailsAsync(
            _activePageId is { } activeId ? new HashSet<Guid> { activeId } : null);
    }

    private void CancelDetailRendering()
    {
        Interlocked.Increment(ref _detailRenderGeneration);
        _detailRenderCancellation?.Cancel();
    }

    private async void PageThumbnailList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSynchronizingSelection)
        {
            return;
        }

        var selectedPage = PageThumbnailList.SelectedItem as PdfPageThumbnail
            ?? GetSelectedPages().FirstOrDefault();
        if (selectedPage is not null)
        {
            SetActivePage(selectedPage.Id);
        }

        UpdateControls();
        if (!_isRenderingThumbnails && !_isOperationBusy && _workspace.HasDocument)
        {
            StatusText.Text = PageThumbnailList.SelectedItems.Count == 0
                ? "Select one or more pages to rotate, crop, or delete."
                : $"{PageThumbnailList.SelectedItems.Count:N0} page(s) selected.";
        }

        if (_viewMode == EditorViewMode.Page)
        {
            await RenderActiveDetailPageAsync();
        }
    }

    private async void PageNavigationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSynchronizingSelection ||
            PageNavigationList.SelectedItem is not PdfPageThumbnail page)
        {
            return;
        }

        _activePageId = page.Id;
        SelectPageIds([page.Id], page.Id);
        await RenderActiveDetailPageAsync();
    }

    private void PageList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox)
        {
            return;
        }

        var container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        _pageDragOrigin = container?.DataContext as PdfPageThumbnail;
        _pageDragStart = _pageDragOrigin is null ? null : e.GetPosition(listBox);
    }

    private void PageList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not ListBox listBox ||
            e.LeftButton != MouseButtonState.Pressed ||
            _pageDragStart is not { } dragStart ||
            _pageDragOrigin is not { } dragOrigin ||
            _isOperationBusy ||
            !_workspace.HasDocument)
        {
            return;
        }

        var current = e.GetPosition(listBox);
        if (Math.Abs(current.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var selectedPages = GetSelectedPages();
        if (!selectedPages.Any(page => page.Id == dragOrigin.Id))
        {
            SelectPageIds([dragOrigin.Id], dragOrigin.Id);
            selectedPages = [dragOrigin];
        }

        var pageIds = selectedPages
            .OrderBy(page => page.PageIndex)
            .Select(page => page.Id)
            .ToArray();
        var data = new DataObject();
        data.SetData(PageDragDataFormat, pageIds);
        try
        {
            DragDrop.DoDragDrop(listBox, data, DragDropEffects.Move);
        }
        finally
        {
            _pageDragStart = null;
            _pageDragOrigin = null;
            ClearPageDropTarget();
        }
    }

    private void PageList_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not ListBox listBox ||
            !e.Data.GetDataPresent(PageDragDataFormat) ||
            _isOperationBusy)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        AutoScrollPageList(listBox, e.GetPosition(listBox));

        var target = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        var dropAfter = false;
        if (target is not null)
        {
            var targetPoint = e.GetPosition(target);
            dropAfter = ReferenceEquals(listBox, PageNavigationList)
                ? targetPoint.Y > target.ActualHeight / 2
                : targetPoint.X > target.ActualWidth / 2;
        }

        SetPageDropTarget(
            target,
            dropAfter,
            ReferenceEquals(listBox, PageNavigationList));
    }

    private void PageList_DragLeave(object sender, DragEventArgs e) =>
        ClearPageDropTarget();

    private async void PageList_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var listBox = sender as ListBox;
        var target = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        var dropAfter = target is not null &&
                        listBox is not null &&
                        (ReferenceEquals(listBox, PageNavigationList)
                            ? e.GetPosition(target).Y > target.ActualHeight / 2
                            : e.GetPosition(target).X > target.ActualWidth / 2);
        var pageIds = e.Data.GetData(PageDragDataFormat) as Guid[];
        ClearPageDropTarget();
        if (pageIds is null || pageIds.Length == 0)
        {
            return;
        }

        var movedIds = pageIds.ToHashSet();
        var targetPage = target?.DataContext as PdfPageThumbnail;
        var originalSlot = targetPage is null
            ? _pages.Count
            : targetPage.PageIndex + (dropAfter ? 1 : 0);
        var insertionIndex = _pages
            .Take(originalSlot)
            .Count(page => !movedIds.Contains(page.Id));
        await ReorderPagesAsync(pageIds, insertionIndex);
    }

    private async Task ReorderPagesAsync(
        IReadOnlyCollection<Guid> movedPageIds,
        int insertionIndex)
    {
        var movedIds = movedPageIds.ToHashSet();
        var movedPages = _pages
            .Where(page => movedIds.Contains(page.Id))
            .OrderBy(page => page.PageIndex)
            .ToList();
        if (movedPages.Count == 0)
        {
            return;
        }

        var reorderedIds = _pages
            .Where(page => !movedIds.Contains(page.Id))
            .Select(page => page.Id)
            .ToList();
        insertionIndex = Math.Clamp(insertionIndex, 0, reorderedIds.Count);
        reorderedIds.InsertRange(insertionIndex, movedPages.Select(page => page.Id));
        if (reorderedIds.SequenceEqual(_pages.Select(page => page.Id)))
        {
            StatusText.Text = "Those pages are already in that position.";
            return;
        }

        EditorHistoryEntry? historyEntry = null;
        var reordered = false;
        try
        {
            BeginOperation($"Moving {movedPages.Count:N0} page(s)...");
            CancelAllRendering();
            historyEntry = await CaptureHistoryEntryAsync(
                $"Reorder {movedPages.Count:N0} page(s)");
            reordered = await Task.Run(() => _workspace.ReorderPages(
                movedPages.Select(page => page.PageIndex),
                insertionIndex));
            if (!reordered)
            {
                StatusText.Text = "Those pages are already in that position.";
                return;
            }

            RecordCompletedEdit(historyEntry);
            MarkDocumentChanged();
            SynchronizePageModels();
            SelectPageIds(movedIds, movedPages[0].Id);
            StatusText.Text = BuildEditCompletedStatus(
                $"Moved {movedPages.Count:N0} page(s).",
                historyEntry is not null);
        }
        catch (Exception exception)
        {
            ShowEditorError("reorder the selected pages", exception);
        }
        finally
        {
            EndOperation();
        }

        if (reordered)
        {
            await RefreshPreviewsAsync(movedIds);
        }
    }

    private void SetPageDropTarget(
        ListBoxItem? target,
        bool dropAfter,
        bool isVertical)
    {
        if (_pageDropTarget == target &&
            _dropAfterTarget == dropAfter &&
            _dropTargetIsVertical == isVertical)
        {
            return;
        }

        ClearPageDropTarget();
        _pageDropTarget = target;
        _dropAfterTarget = dropAfter;
        _dropTargetIsVertical = isVertical;
        if (target is null)
        {
            return;
        }

        target.BorderBrush = new SolidColorBrush(Color.FromRgb(216, 117, 0));
        target.BorderThickness = isVertical
            ? dropAfter
                ? new Thickness(2, 2, 2, 5)
                : new Thickness(2, 5, 2, 2)
            : dropAfter
                ? new Thickness(2, 2, 5, 2)
                : new Thickness(5, 2, 2, 2);
    }

    private void ClearPageDropTarget()
    {
        if (_pageDropTarget is not null)
        {
            _pageDropTarget.ClearValue(Border.BorderBrushProperty);
            _pageDropTarget.ClearValue(Border.BorderThicknessProperty);
        }

        _pageDropTarget = null;
        _dropAfterTarget = false;
        _dropTargetIsVertical = false;
    }

    private static void AutoScrollPageList(ListBox listBox, Point pointer)
    {
        var scrollViewer = FindDescendant<ScrollViewer>(listBox);
        if (scrollViewer is null)
        {
            return;
        }

        const double edge = 42;
        if (pointer.Y < edge)
        {
            scrollViewer.LineUp();
        }
        else if (pointer.Y > listBox.ActualHeight - edge)
        {
            scrollViewer.LineDown();
        }
    }

    private void SetActivePage(Guid pageId)
    {
        _activePageId = pageId;
        _isSynchronizingSelection = true;
        try
        {
            PageNavigationList.SelectedItem = _pages.FirstOrDefault(page => page.Id == pageId);
        }
        finally
        {
            _isSynchronizingSelection = false;
        }

        UpdatePagePosition();
        UpdateControls();
    }

    private async Task NavigatePageAsync(int offset)
    {
        if (_pages.Count == 0)
        {
            return;
        }

        var currentIndex = ActivePage?.PageIndex ?? 0;
        var nextIndex = Math.Clamp(currentIndex + offset, 0, _pages.Count - 1);
        var nextPage = _pages[nextIndex];
        SelectPageIds([nextPage.Id], nextPage.Id);
        await RenderActiveDetailPageAsync();
    }

    private void SetEditorView(EditorViewMode mode)
    {
        _viewMode = mode;
        var isGrid = mode == EditorViewMode.ThumbnailGrid;
        ThumbnailGridPanel.Visibility = isGrid ? Visibility.Visible : Visibility.Collapsed;
        PageViewPanel.Visibility = isGrid ? Visibility.Collapsed : Visibility.Visible;
        ThumbnailZoomPanel.Visibility = isGrid ? Visibility.Visible : Visibility.Collapsed;
        GridViewToggle.IsChecked = isGrid;
        PageViewToggle.IsChecked = !isGrid;
        ViewDescriptionText.Text = isGrid
            ? "Batch-select pages for editing, then drag them to reorder."
            : "Inspect one page closely. Drag the page to pan when zoomed in.";

        if (!isGrid && ActivePage is null && _pages.Count > 0)
        {
            SelectPageIds([_pages[0].Id], _pages[0].Id);
        }

        UpdateControls();
        ScheduleViewportThumbnailRender();
    }

    private void UpdatePagePosition()
    {
        var page = ActivePage;
        if (page is null)
        {
            PagePositionText.Text = "Page 0 of 0";
            DetailEditSummaryText.Text = "No page selected";
            PreviousPageButton.IsEnabled = false;
            NextPageButton.IsEnabled = false;
            return;
        }

        PagePositionText.Text = $"Page {page.PageNumber:N0} of {_pages.Count:N0}";
        DetailEditSummaryText.Text = page.EditSummary == "original"
            ? "No edits on this page"
            : $"Edits: {page.EditSummary}";
        PreviousPageButton.IsEnabled = !_isOperationBusy && page.PageIndex > 0;
        NextPageButton.IsEnabled = !_isOperationBusy && page.PageIndex < _pages.Count - 1;
    }

    private void DetailZoomSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingDetailZoom ||
            DetailPageImage is null ||
            DetailZoomText is null ||
            PageViewScrollViewer is null)
        {
            return;
        }

        _detailFitMode = DetailFitMode.None;
        ApplyDetailZoom(e.NewValue, preserveViewportCenter: true);
    }

    private void ApplyDetailZoom(double zoomPercent, bool preserveViewportCenter)
    {
        if (DetailPageImage is null ||
            DetailZoomText is null ||
            PageViewScrollViewer is null)
        {
            return;
        }

        if (DetailPageImage.Source is not BitmapSource image)
        {
            DetailZoomText.Text = $"{zoomPercent:0}%";
            return;
        }

        var oldWidth = Math.Max(1, DetailPageImage.Width);
        var oldHeight = Math.Max(1, DetailPageImage.Height);
        var viewportCenterX = PageViewScrollViewer.HorizontalOffset +
                              (PageViewScrollViewer.ViewportWidth / 2);
        var viewportCenterY = PageViewScrollViewer.VerticalOffset +
                              (PageViewScrollViewer.ViewportHeight / 2);
        var scale = zoomPercent / 100d;
        var newWidth = image.PixelWidth * scale;
        var newHeight = image.PixelHeight * scale;

        DetailPageImage.Width = newWidth;
        DetailPageImage.Height = newHeight;
        DetailZoomText.Text = $"{zoomPercent:0}%";

        if (preserveViewportCenter &&
            PageViewScrollViewer.ViewportWidth > 0 &&
            PageViewScrollViewer.ViewportHeight > 0)
        {
            var widthRatio = newWidth / oldWidth;
            var heightRatio = newHeight / oldHeight;
            PageViewScrollViewer.ScrollToHorizontalOffset(
                (viewportCenterX * widthRatio) - (PageViewScrollViewer.ViewportWidth / 2));
            PageViewScrollViewer.ScrollToVerticalOffset(
                (viewportCenterY * heightRatio) - (PageViewScrollViewer.ViewportHeight / 2));
        }
    }

    private void ApplyDetailFit()
    {
        if (DetailPageImage is null ||
            PageViewScrollViewer is null ||
            DetailZoomSlider is null ||
            DetailPageImage.Source is not BitmapSource image ||
            _detailFitMode == DetailFitMode.None)
        {
            return;
        }

        var availableWidth = Math.Max(1, PageViewScrollViewer.ViewportWidth - 64);
        var availableHeight = Math.Max(1, PageViewScrollViewer.ViewportHeight - 64);
        var zoom = _detailFitMode == DetailFitMode.FitWidth
            ? availableWidth / image.PixelWidth * 100
            : Math.Min(
                availableWidth / image.PixelWidth,
                availableHeight / image.PixelHeight) * 100;
        zoom = Math.Clamp(zoom, DetailZoomSlider.Minimum, DetailZoomSlider.Maximum);

        _isUpdatingDetailZoom = true;
        try
        {
            DetailZoomSlider.Value = zoom;
        }
        finally
        {
            _isUpdatingDetailZoom = false;
        }

        ApplyDetailZoom(zoom, preserveViewportCenter: false);
        PageViewScrollViewer.ScrollToHorizontalOffset(0);
        PageViewScrollViewer.ScrollToVerticalOffset(0);
    }

    private void AdjustCurrentViewZoom(bool zoomIn)
    {
        if (_viewMode == EditorViewMode.ThumbnailGrid)
        {
            var current = ThumbnailZoomSlider.Value;
            var step = current >= 500 ? 100 : current >= 250 ? 50 : 25;
            ThumbnailZoomSlider.Value = Math.Clamp(
                current + (zoomIn ? step : -step),
                ThumbnailZoomSlider.Minimum,
                ThumbnailZoomSlider.Maximum);
            return;
        }

        _detailFitMode = DetailFitMode.None;
        var detailCurrent = DetailZoomSlider.Value;
        var detailStep = detailCurrent >= 300 ? 100 : detailCurrent >= 100 ? 25 : 10;
        DetailZoomSlider.Value = Math.Clamp(
            detailCurrent + (zoomIn ? detailStep : -detailStep),
            DetailZoomSlider.Minimum,
            DetailZoomSlider.Maximum);
    }

    private void PageViewScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_viewMode == EditorViewMode.Page && _detailFitMode != DetailFitMode.None)
        {
            ApplyDetailFit();
        }
    }

    private void PageViewScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        e.Handled = true;
        AdjustCurrentViewZoom(e.Delta > 0);
    }

    private void PageViewScrollViewer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ScrollBar>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        _isPanning = true;
        _panStartPoint = e.GetPosition(PageViewScrollViewer);
        _panStartHorizontalOffset = PageViewScrollViewer.HorizontalOffset;
        _panStartVerticalOffset = PageViewScrollViewer.VerticalOffset;
        PageViewScrollViewer.Cursor = Cursors.SizeAll;
        PageViewScrollViewer.CaptureMouse();
        e.Handled = true;
    }

    private void PageViewScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning)
        {
            return;
        }

        var currentPoint = e.GetPosition(PageViewScrollViewer);
        PageViewScrollViewer.ScrollToHorizontalOffset(
            _panStartHorizontalOffset - (currentPoint.X - _panStartPoint.X));
        PageViewScrollViewer.ScrollToVerticalOffset(
            _panStartVerticalOffset - (currentPoint.Y - _panStartPoint.Y));
    }

    private void PageViewScrollViewer_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        StopPanning();

    private void PageViewScrollViewer_LostMouseCapture(object sender, MouseEventArgs e) =>
        StopPanning();

    private void StopPanning()
    {
        if (!_isPanning)
        {
            return;
        }

        _isPanning = false;
        PageViewScrollViewer.Cursor = Cursors.Arrow;
        if (PageViewScrollViewer.IsMouseCaptured)
        {
            PageViewScrollViewer.ReleaseMouseCapture();
        }
    }

    private void ThumbnailZoomSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e) =>
        ApplyThumbnailZoom(e.NewValue);

    private void PageThumbnailList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        e.Handled = true;
        AdjustCurrentViewZoom(e.Delta > 0);
    }

    private void ApplyThumbnailZoom(double thumbnailLongEdge)
    {
        foreach (var page in _pages)
        {
            page.ThumbnailLongEdge = thumbnailLongEdge;
        }

        if (FindDescendant<VirtualizingWrapPanel>(PageThumbnailList) is { } panel)
        {
            panel.ThumbnailSize = thumbnailLongEdge;
        }

        ScheduleViewportThumbnailRender();
    }

    private List<PdfPageThumbnail> GetSelectedPages() =>
        PageThumbnailList.SelectedItems
            .Cast<PdfPageThumbnail>()
            .OrderBy(page => page.PageIndex)
            .ToList();

    private void SelectPageIndexes(IEnumerable<int> pageIndexes)
    {
        var ids = pageIndexes
            .Distinct()
            .Where(index => index >= 0 && index < _pages.Count)
            .Order()
            .Select(index => _pages[index].Id)
            .ToList();
        SelectPageIds(ids, ids.FirstOrDefault());
    }

    private void SelectPageIds(IEnumerable<Guid> pageIds, Guid? preferredActivePageId)
    {
        var requestedIds = pageIds.Distinct().ToHashSet();
        var selectedPages = _pages.Where(page => requestedIds.Contains(page.Id)).ToList();
        _isSynchronizingSelection = true;
        try
        {
            PageThumbnailList.SelectedItems.Clear();
            foreach (var page in selectedPages)
            {
                PageThumbnailList.SelectedItems.Add(page);
            }

            var activePage = preferredActivePageId is { } preferred
                ? selectedPages.FirstOrDefault(page => page.Id == preferred)
                : null;
            activePage ??= selectedPages.FirstOrDefault();
            activePage ??= _pages.FirstOrDefault();
            _activePageId = activePage?.Id;
            PageNavigationList.SelectedItem = activePage;
        }
        finally
        {
            _isSynchronizingSelection = false;
        }

        if (PageThumbnailList.SelectedItem is { } selectedItem)
        {
            PageThumbnailList.ScrollIntoView(selectedItem);
        }

        if (PageNavigationList.SelectedItem is { } navigationItem)
        {
            PageNavigationList.ScrollIntoView(navigationItem);
        }

        UpdatePagePosition();
        UpdateControls();
    }

    private void BeginOperation(string status)
    {
        _isOperationBusy = true;
        StatusText.Text = status;
        UpdateControls();
    }

    private void EndOperation()
    {
        _isOperationBusy = false;
        UpdateControls();
    }

    private void UpdateControls()
    {
        var hasDocument = _workspace.HasDocument;
        var selectionCount = PageThumbnailList.SelectedItems.Count;
        var hasSelection = selectionCount > 0;
        var canEditSelection = hasDocument && hasSelection && !_isOperationBusy;

        RotateLeftButton.IsEnabled = canEditSelection;
        RotateRightButton.IsEnabled = canEditSelection;
        CropButton.IsEnabled = canEditSelection;
        DeleteButton.IsEnabled = canEditSelection && selectionCount < _pages.Count;
        SaveButton.IsEnabled = hasDocument && _hasUnsavedChanges && !_isOperationBusy;
        UndoButton.IsEnabled = !_isOperationBusy && _undoHistory.Count > 0;
        RedoButton.IsEnabled = !_isOperationBusy && _redoHistory.Count > 0;
        RotateLeftMenuItem.IsEnabled = RotateLeftButton.IsEnabled;
        RotateRightMenuItem.IsEnabled = RotateRightButton.IsEnabled;
        CropMenuItem.IsEnabled = CropButton.IsEnabled;
        DeleteMenuItem.IsEnabled = DeleteButton.IsEnabled;
        SaveMenuItem.IsEnabled = SaveButton.IsEnabled;
        UndoMenuItem.IsEnabled = UndoButton.IsEnabled;
        RedoMenuItem.IsEnabled = RedoButton.IsEnabled;
        UndoMenuItem.Header = _undoHistory.Count == 0
            ? "_Undo"
            : $"_Undo {_undoHistory[^1].Description}";
        RedoMenuItem.Header = _redoHistory.Count == 0
            ? "_Redo"
            : $"_Redo {_redoHistory[^1].Description}";
        UndoButton.ToolTip = _undoHistory.Count == 0
            ? "Nothing to undo"
            : $"Undo {_undoHistory[^1].Description} (Ctrl+Z)";
        RedoButton.ToolTip = _redoHistory.Count == 0
            ? "Nothing to redo"
            : $"Redo {_redoHistory[^1].Description} (Ctrl+Y)";
        var hasBackup = _workspace.SessionBackupPath is { } backupPath &&
                        File.Exists(backupPath);
        OpenBackupFolderMenuItem.IsEnabled = hasBackup;
        RestoreBackupMenuItem.IsEnabled = hasBackup && !_isOperationBusy;
        OpenBackupFolderButton.Visibility = !hasBackup
            ? Visibility.Collapsed
            : Visibility.Visible;
        RestoreBackupButton.Visibility = !hasBackup
            ? Visibility.Collapsed
            : Visibility.Visible;
        RestoreBackupButton.IsEnabled = !_isOperationBusy;
        RescanReportButton.Visibility = !hasBackup
            ? Visibility.Collapsed
            : Visibility.Visible;
        UpdatePagePosition();
    }

    private void UpdateDocumentHeading()
    {
        var sourcePath = _workspace.SourcePath;
        var fileName = sourcePath is null ? "No PDF open" : Path.GetFileName(sourcePath);
        DocumentNameText.Text = _hasUnsavedChanges ? $"* {fileName}" : fileName;
        DocumentPathText.Text = sourcePath ?? string.Empty;
        DocumentPathText.ToolTip = sourcePath;
        SaveStateText.Text = !_workspace.HasDocument
            ? "No document"
            : _hasUnsavedChanges
                ? "Unsaved changes"
                : "Saved";
        SaveStateText.Foreground = !_workspace.HasDocument
            ? new SolidColorBrush(Color.FromRgb(93, 104, 115))
            : _hasUnsavedChanges
                ? new SolidColorBrush(Color.FromRgb(161, 92, 0))
                : new SolidColorBrush(Color.FromRgb(39, 98, 53));
        Title = _hasUnsavedChanges
            ? $"* {fileName} - Archive Assist PDF Editor"
            : $"{fileName} - Archive Assist PDF Editor";
        EmptyStateText.Visibility = _workspace.HasDocument
            ? Visibility.Collapsed
            : Visibility.Visible;
        UpdateControls();
    }

    private string BuildReadyStatus()
    {
        var selectionCount = PageThumbnailList.SelectedItems.Count;
        return _viewMode == EditorViewMode.Page && ActivePage is { } activePage
            ? $"Ready. Viewing page {activePage.PageNumber:N0} of {_pages.Count:N0}."
            : selectionCount > 0
                ? $"Ready. {selectionCount:N0} page(s) selected."
                : $"Ready. {_pages.Count:N0} page(s); select pages to edit.";
    }

    private void ShowEditorError(string action, Exception exception)
    {
        MessageBox.Show(
            this,
            $"Archive Assist could not {action}.\n\n{exception.GetBaseException().Message}",
            "PDF editor",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void RestoreEditorSettings()
    {
        if (_settings is not { } settings)
        {
            return;
        }

        if (double.IsFinite(settings.EditorWindowWidth) &&
            settings.EditorWindowWidth >= MinWidth)
        {
            Width = Math.Min(settings.EditorWindowWidth, SystemParameters.WorkArea.Width);
        }

        if (double.IsFinite(settings.EditorWindowHeight) &&
            settings.EditorWindowHeight >= MinHeight)
        {
            Height = Math.Min(settings.EditorWindowHeight, SystemParameters.WorkArea.Height);
        }

        if (settings.EditorWindowLeft is { } left &&
            settings.EditorWindowTop is { } top &&
            double.IsFinite(left) &&
            double.IsFinite(top))
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

        ThumbnailZoomSlider.Value = Math.Clamp(
            settings.EditorThumbnailZoom,
            ThumbnailZoomSlider.Minimum,
            ThumbnailZoomSlider.Maximum);
        DetailZoomSlider.Value = Math.Clamp(
            settings.EditorDetailZoom,
            DetailZoomSlider.Minimum,
            DetailZoomSlider.Maximum);
        if (Enum.TryParse<DetailFitMode>(
                settings.EditorDetailFitMode,
                ignoreCase: true,
                out var detailFitMode))
        {
            _detailFitMode = detailFitMode;
        }

        SetEditorView(
            string.Equals(
                settings.EditorViewMode,
                nameof(EditorViewMode.Page),
                StringComparison.OrdinalIgnoreCase)
                ? EditorViewMode.Page
                : EditorViewMode.ThumbnailGrid);

        if (settings.IsEditorMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void SaveEditorSettings()
    {
        if (_settings is not { } settings || _settingsService is null)
        {
            return;
        }

        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;
        if (double.IsFinite(bounds.Width) && bounds.Width >= MinWidth)
        {
            settings.EditorWindowWidth = bounds.Width;
        }

        if (double.IsFinite(bounds.Height) && bounds.Height >= MinHeight)
        {
            settings.EditorWindowHeight = bounds.Height;
        }

        if (double.IsFinite(bounds.Left) && double.IsFinite(bounds.Top))
        {
            settings.EditorWindowLeft = bounds.Left;
            settings.EditorWindowTop = bounds.Top;
        }

        settings.IsEditorMaximized = WindowState == WindowState.Maximized;
        settings.EditorThumbnailZoom = ThumbnailZoomSlider.Value;
        settings.EditorDetailZoom = DetailZoomSlider.Value;
        settings.EditorViewMode = _viewMode.ToString();
        settings.EditorDetailFitMode = _detailFitMode.ToString();
        _settingsService.Save(settings);
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            try
            {
                current = VisualTreeHelper.GetParent(current);
            }
            catch (InvalidOperationException)
            {
                current = current is FrameworkContentElement contentElement
                    ? contentElement.Parent
                    : LogicalTreeHelper.GetParent(current);
            }
        }

        return null;
    }

    private static T? FindDescendant<T>(DependencyObject source) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(source); index++)
        {
            var child = VisualTreeHelper.GetChild(source, index);
            if (child is T match)
            {
                return match;
            }

            var nested = FindDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var modifiers = Keyboard.Modifiers;

        if (e.Key == Key.O && modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            _ = SelectAndOpenPdfAsync();
        }
        else if (e.Key == Key.S && modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            SaveChanges();
        }
        else if (e.Key == Key.Z && modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            _ = RestoreHistoryAsync(_undoHistory, _redoHistory, "Undoing");
        }
        else if (e.Key == Key.Y && modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            _ = RestoreHistoryAsync(_redoHistory, _undoHistory, "Redoing");
        }
        else if (e.Key == Key.A && modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            SelectAll_Click(this, new RoutedEventArgs());
        }
        else if (e.Key == Key.D1 && modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            SetEditorView(EditorViewMode.ThumbnailGrid);
        }
        else if (e.Key == Key.D2 && modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            SetEditorView(EditorViewMode.Page);
            _ = RenderActiveDetailPageAsync();
        }
        else if (e.Key == Key.D0 && modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            _detailFitMode = DetailFitMode.FitPage;
            SetEditorView(EditorViewMode.Page);
            _ = RenderActiveDetailPageAsync();
            ApplyDetailFit();
        }
        else if (e.Key == Key.D0 &&
                 modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            _detailFitMode = DetailFitMode.FitWidth;
            SetEditorView(EditorViewMode.Page);
            _ = RenderActiveDetailPageAsync();
            ApplyDetailFit();
        }
        else if ((e.Key == Key.OemPlus || e.Key == Key.Add) &&
                 (modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            e.Handled = true;
            AdjustCurrentViewZoom(zoomIn: true);
        }
        else if ((e.Key == Key.OemMinus || e.Key == Key.Subtract) &&
                 (modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            e.Handled = true;
            AdjustCurrentViewZoom(zoomIn: false);
        }
        else if (e.Key == Key.PageUp && _viewMode == EditorViewMode.Page)
        {
            e.Handled = true;
            _ = NavigatePageAsync(-1);
        }
        else if (e.Key == Key.PageDown && _viewMode == EditorViewMode.Page)
        {
            e.Handled = true;
            _ = NavigatePageAsync(1);
        }
        else if (e.Key == Key.Delete && DeleteButton.IsEnabled)
        {
            e.Handled = true;
            _ = DeleteSelectedPagesAsync();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_isOperationBusy)
        {
            MessageBox.Show(
                this,
                "Wait for the current PDF operation to finish before closing the editor.",
                "PDF editor is busy",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            e.Cancel = true;
            return;
        }

        if (!ConfirmSaveOrDiscardUnsavedChanges("close the editor"))
        {
            e.Cancel = true;
            return;
        }

        CancelAllRendering();
        SaveEditorSettings();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _thumbnailViewportTimer.Stop();
        _workspace.Dispose();
        base.OnClosed(e);
    }

    private sealed record EditorHistoryEntry(
        string Description,
        PdfEditWorkspaceSnapshot Snapshot,
        bool WasDirty,
        IReadOnlyList<Guid> SelectedPageIds,
        Guid? ActivePageId);

    private enum EditorViewMode
    {
        ThumbnailGrid,
        Page
    }

    private enum DetailFitMode
    {
        None,
        FitPage,
        FitWidth
    }
}
