using System.Windows;
using System.Windows.Controls;
using ArchiveAssist.App.Models;
using ArchiveAssist.App.Services;
using ArchiveAssist.Core.Models;

namespace ArchiveAssist.App;

public partial class PreferencesWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ISettingsService _settingsService;

    public PreferencesWindow(
        AppSettings settings,
        ISettingsService settingsService)
    {
        _settings = settings;
        _settingsService = settingsService;
        InitializeComponent();

        ThresholdCombo.ItemsSource = PageSizePreset.BuiltIn;
        ThresholdCombo.SelectedItem = PageSizePreset.BuiltIn.FirstOrDefault(
            preset => string.Equals(
                preset.Name,
                settings.ThresholdName,
                StringComparison.OrdinalIgnoreCase))
            ?? PageSizePreset.Default;
        QaModeCombo.ItemsSource = PdfQaMode.BuiltIn;
        QaModeCombo.SelectedItem = PdfQaMode.BuiltIn.FirstOrDefault(
            mode => string.Equals(
                mode.Name,
                settings.QaModeName,
                StringComparison.OrdinalIgnoreCase))
            ?? PdfQaMode.StandardQa;
        MaxPagesTextBox.Text = Math.Max(1, settings.MaxPagesPerPdf).ToString();
        IncludeHeadersCheckBox.IsChecked = settings.IncludeClipboardHeaders;
        AdvancedExpandedCheckBox.IsChecked = settings.MainAdvancedSettingsExpanded;
        ShowMainWelcomeCheckBox.IsChecked = !settings.HasSeenMainWelcome;
        ShowEditorWelcomeCheckBox.IsChecked = !settings.HasSeenEditorWelcome;
        EditorThumbnailSlider.Value = Math.Clamp(settings.EditorThumbnailZoom, 100, 1000);
        EditorDetailSlider.Value = Math.Clamp(settings.EditorDetailZoom, 10, 800);
        EditorViewCombo.SelectedIndex = string.Equals(
            settings.EditorViewMode,
            "Page",
            StringComparison.OrdinalIgnoreCase)
            ? 1
            : 0;
        var safetyMode = FileSafetyModes.Normalize(settings.FileSafetyMode);
        FileSafetyModeCombo.SelectedItem = FileSafetyModeCombo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item =>
                string.Equals(item.Tag as string, safetyMode, StringComparison.Ordinal));
        RecoveryRetentionCombo.SelectedItem = RecoveryRetentionCombo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item =>
                int.TryParse(item.Tag as string, out var days) &&
                days == settings.RecoveryRetentionDays)
            ?? RecoveryRetentionCombo.Items
                .OfType<ComboBoxItem>()
                .First(item => string.Equals(item.Tag as string, "30", StringComparison.Ordinal));
        UpdateQaDescription();
        UpdateSafetyModeDescription();
    }

    private void QaModeCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e) =>
        UpdateQaDescription();

    private void ResetReportLayout_Click(object sender, RoutedEventArgs e)
    {
        _settings.ReportColumnWidths.Clear();
        _settings.ReportSortProperty = string.Empty;
        _settings.ReportSortDirection = string.Empty;
        ReportResetText.Text = "The report layout will return to its defaults.";
    }

    private void FileSafetyModeCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e) =>
        UpdateSafetyModeDescription();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(MaxPagesTextBox.Text, out var maxPages) || maxPages <= 0)
        {
            MessageBox.Show(
                this,
                "Enter a maximum PDF page count greater than zero.",
                "Preferences",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            MaxPagesTextBox.Focus();
            MaxPagesTextBox.SelectAll();
            return;
        }

        if (ThresholdCombo.SelectedItem is PageSizePreset threshold)
        {
            _settings.ThresholdName = threshold.Name;
        }

        if (QaModeCombo.SelectedItem is PdfQaMode qaMode)
        {
            _settings.QaModeName = qaMode.Name;
        }

        _settings.MaxPagesPerPdf = maxPages;
        _settings.IncludeClipboardHeaders = IncludeHeadersCheckBox.IsChecked == true;
        _settings.MainAdvancedSettingsExpanded = AdvancedExpandedCheckBox.IsChecked == true;
        _settings.HasSeenMainWelcome = ShowMainWelcomeCheckBox.IsChecked != true;
        _settings.HasSeenEditorWelcome = ShowEditorWelcomeCheckBox.IsChecked != true;
        _settings.EditorThumbnailZoom = EditorThumbnailSlider.Value;
        _settings.EditorDetailZoom = EditorDetailSlider.Value;
        _settings.EditorViewMode =
            (EditorViewCombo.SelectedItem as ComboBoxItem)?.Tag as string
            ?? "ThumbnailGrid";
        _settings.FileSafetyMode = FileSafetyModes.Normalize(
            (FileSafetyModeCombo.SelectedItem as ComboBoxItem)?.Tag as string);
        _settings.RecoveryRetentionDays =
            int.TryParse(
                (RecoveryRetentionCombo.SelectedItem as ComboBoxItem)?.Tag as string,
                out var retentionDays)
                ? retentionDays
                : 30;
        _settingsService.Save(_settings);
        DialogResult = true;
    }

    private void UpdateQaDescription()
    {
        if (QaModeDescriptionText is not null)
        {
            QaModeDescriptionText.Text =
                (QaModeCombo.SelectedItem as PdfQaMode)?.Description
                ?? string.Empty;
        }
    }

    private void UpdateSafetyModeDescription()
    {
        if (SafetyModeDescriptionText is null)
        {
            return;
        }

        SafetyModeDescriptionText.Text = FileSafetyModes.Normalize(
            (FileSafetyModeCombo.SelectedItem as ComboBoxItem)?.Tag as string) switch
        {
            FileSafetyModes.AlwaysAsk =>
                "Each PDF Editor save asks whether to safely update the open file or save an edited copy.",
            FileSafetyModes.SaveCopies =>
                "PDF Editor saves prompt for a new copy and continue editing that copy. The original remains unchanged.",
            _ =>
                "PDF Editor saves directly to the open file after retaining its original version in Recovery Center."
        };
    }
}
