using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ArchiveAssist.App.Services;
using ArchiveAssist.Core.Models;
using ArchiveAssist.Core.Services;

namespace ArchiveAssist.App;

public partial class RecoveryCenterWindow : Window
{
    private readonly IFileRecoveryService _recoveryService;
    private readonly int _retentionDays;
    private readonly string? _originalPathFilter;
    private readonly ObservableCollection<FileRecoveryPoint> _points = [];

    public RecoveryCenterWindow(
        IFileRecoveryService recoveryService,
        int retentionDays,
        string? originalPathFilter = null)
    {
        _recoveryService = recoveryService;
        _retentionDays = retentionDays;
        _originalPathFilter = string.IsNullOrWhiteSpace(originalPathFilter)
            ? null
            : Path.GetFullPath(originalPathFilter);
        InitializeComponent();
        RecoveryGrid.ItemsSource = _points;
        RetentionText.Text = retentionDays > 0
            ? $"New recovery points are retained for {retentionDays:N0} days. Restoring a file first preserves its current version as another recovery point."
            : "Recovery points are kept until you delete them. Restoring a file first preserves its current version as another recovery point.";
        Loaded += (_, _) => RefreshPoints();
    }

    public HashSet<string> RestoredPaths { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    private FileRecoveryPoint? SelectedPoint =>
        RecoveryGrid.SelectedItem as FileRecoveryPoint;

    private void RefreshPoints(string? status = null)
    {
        var selectedId = SelectedPoint?.Id;
        List<FileRecoveryPoint> points;
        try
        {
            points = _recoveryService.GetRecoveryPoints(includeUnavailable: true)
                .Where(point =>
                    _originalPathFilter is null ||
                    string.Equals(
                        point.OriginalPath,
                        _originalPathFilter,
                        StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            points = [];
            status =
                "Recovery Center could not read its index. No recovery files were changed. " +
                exception.Message;
        }
        _points.Clear();
        foreach (var point in points)
        {
            _points.Add(point);
        }

        RecoveryGrid.SelectedItem = _points.FirstOrDefault(point => point.Id == selectedId);
        EmptyStatePanel.Visibility = _points.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        StatusText.Text = status ?? $"{_points.Count:N0} recovery point(s) available.";
        UpdateButtons();
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var point = SelectedPoint;
        if (point is null)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Restore {point.FileName} to its version from {point.CreatedAtUtc.LocalDateTime:g}?\n\n" +
            "The current version will be retained as another recovery point first.",
            "Restore file",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true, "Restoring the selected recovery point...");
        try
        {
            var result = await Task.Run(() =>
                _recoveryService.Restore(point.Id, _retentionDays));
            RestoredPaths.Add(result.RestoredPath);
            RefreshPoints($"Restored {Path.GetFileName(result.RestoredPath)}. The previous current version was retained.");
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Restore failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            RefreshPoints("The file was not restored.");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OpenCurrent_Click(object sender, RoutedEventArgs e)
    {
        var point = SelectedPoint;
        if (point is null || !File.Exists(point.OriginalPath))
        {
            StatusText.Text = "The current file is no longer available at its original location.";
            return;
        }

        try
        {
            PathLauncher.Open(point.OriginalPath);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Could not open file",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var point = SelectedPoint;
        if (point is null)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Permanently delete this recovery point for {point.FileName}?\n\n" +
            "This does not delete or change the current file.",
            "Delete recovery point",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        var deleted = _recoveryService.Delete(point.Id);
        RefreshPoints(deleted
            ? $"Deleted the recovery point for {point.FileName}."
            : "That recovery point was already unavailable.");
    }

    private void CleanExpired_Click(object sender, RoutedEventArgs e)
    {
        var removed = _recoveryService.CleanupExpired();
        RefreshPoints(removed == 0
            ? "No expired recovery points were found."
            : $"Removed {removed:N0} expired recovery point(s).");
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshPoints();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void RecoveryGrid_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e) =>
        UpdateButtons();

    private void UpdateButtons()
    {
        var point = SelectedPoint;
        RestoreButton.IsEnabled = point?.IsAvailable == true;
        OpenCurrentButton.IsEnabled = point is not null && File.Exists(point.OriginalPath);
        DeleteButton.IsEnabled = point is not null;
    }

    private void SetBusy(bool isBusy, string? status = null)
    {
        RecoveryGrid.IsEnabled = !isBusy;
        if (status is not null)
        {
            StatusText.Text = status;
        }

        if (isBusy)
        {
            RestoreButton.IsEnabled = false;
            OpenCurrentButton.IsEnabled = false;
            DeleteButton.IsEnabled = false;
        }
        else
        {
            UpdateButtons();
        }
    }
}
