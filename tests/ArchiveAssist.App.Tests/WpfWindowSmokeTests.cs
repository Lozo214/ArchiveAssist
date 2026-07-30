using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ArchiveAssist.App.Controls;

namespace ArchiveAssist.App.Tests;

public sealed class WpfWindowSmokeTests
{
    [Fact]
    public void MainAndEditorWindowsLoadTheirXaml()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var mainWindow = new MainWindow();
                var editorWindow = new PdfEditorWindow();
                var preferencesWindow = new PreferencesWindow(
                    new ArchiveAssist.App.Models.AppSettings(),
                    new ArchiveAssist.App.Services.JsonSettingsService(
                        Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json")));
                var recoveryCenter = new RecoveryCenterWindow(
                    new ArchiveAssist.Core.Services.FileRecoveryService(
                        Path.Combine(Path.GetTempPath(), $"ArchiveAssist-Recovery-{Guid.NewGuid():N}")),
                    30);
                var renameWindow = new RenameItemWindow("Document.pdf", isFolder: false);

                Assert.Equal("Archive Assist", mainWindow.Title);
                Assert.Contains("Archive Assist PDF Editor", editorWindow.Title);
                Assert.NotNull(mainWindow.FindName("DiscoveryPreviewTab"));
                Assert.NotNull(mainWindow.FindName("ReportTab"));
                Assert.NotNull(mainWindow.FindName("FileStructureTab"));
                Assert.IsType<MenuItem>(mainWindow.FindName("RecentPdfsMenuItem"));
                Assert.IsType<Border>(mainWindow.FindName("SelectionDropZone"));
                Assert.IsType<Border>(mainWindow.FindName("CompletionBanner"));
                Assert.IsType<TextBox>(mainWindow.FindName("ReportSearchBox"));
                Assert.IsType<Grid>(editorWindow.FindName("ThumbnailGridPanel"));
                Assert.IsType<Grid>(editorWindow.FindName("PageViewPanel"));
                var detailToolbar = Assert.IsType<Border>(editorWindow.FindName("DetailToolbar"));
                Assert.Equal(1, Grid.GetRow(detailToolbar));
                var detailPreview = Assert.IsType<ScrollViewer>(
                    editorWindow.FindName("PageViewScrollViewer"));
                Assert.Equal(0, Grid.GetRow(detailPreview));
                Assert.IsType<MenuItem>(editorWindow.FindName("RecentPdfsMenuItem"));
                Assert.IsType<MenuItem>(editorWindow.FindName("RestoreBackupMenuItem"));
                Assert.IsType<Border>(editorWindow.FindName("EditorWelcomePanel"));
                var detailZoom = Assert.IsType<Slider>(editorWindow.FindName("DetailZoomSlider"));
                Assert.Equal(800, detailZoom.Maximum);
                var thumbnailZoom = Assert.IsType<Slider>(editorWindow.FindName("ThumbnailZoomSlider"));
                Assert.Equal(1000, thumbnailZoom.Maximum);
                Assert.Equal(
                    "Archive Assist Preferences",
                    preferencesWindow.Title);
                Assert.IsType<ComboBox>(preferencesWindow.FindName("FileSafetyModeCombo"));
                Assert.IsType<ComboBox>(preferencesWindow.FindName("RecoveryRetentionCombo"));
                Assert.IsType<DataGrid>(recoveryCenter.FindName("RecoveryGrid"));
                Assert.Equal("Recovery Center - Archive Assist", recoveryCenter.Title);
                Assert.Equal("Rename - Archive Assist", renameWindow.Title);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The WPF window construction test timed out.");
        Assert.Null(failure);
    }

    [Fact]
    public void ThumbnailPanelOnlyRealizesItemsNearTheViewport()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var listBox = new ListBox
                {
                    Width = 800,
                    Height = 600,
                    ItemsPanel = new ItemsPanelTemplate(
                        new FrameworkElementFactory(typeof(VirtualizingWrapPanel)))
                };
                listBox.SetValue(ScrollViewer.CanContentScrollProperty, true);
                listBox.SetValue(VirtualizingPanel.IsVirtualizingProperty, true);
                listBox.SetValue(
                    VirtualizingPanel.VirtualizationModeProperty,
                    VirtualizationMode.Recycling);
                for (var index = 0; index < 1000; index++)
                {
                    listBox.Items.Add($"Page {index + 1}");
                }

                var window = new Window
                {
                    Width = 820,
                    Height = 640,
                    Left = -10000,
                    Top = -10000,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None,
                    Content = listBox
                };
                try
                {
                    window.Show();
                    listBox.UpdateLayout();
                    var panel = FindDescendant<VirtualizingWrapPanel>(listBox)
                        ?? throw new InvalidOperationException(
                            "The virtualizing panel was not created.");

                    Assert.InRange(panel.RealizedItemCount, 1, 80);
                    Assert.True(panel.ExtentHeight > panel.ViewportHeight);

                    listBox.ScrollIntoView(listBox.Items[999]);
                    listBox.UpdateLayout();

                    Assert.True(panel.VerticalOffset > 0);
                    Assert.InRange(panel.RealizedItemCount, 1, 80);
                    Assert.NotNull(
                        listBox.ItemContainerGenerator.ContainerFromItem(listBox.Items[999]));
                }
                finally
                {
                    window.Close();
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The virtualization test timed out.");
        Assert.Null(failure);
    }

    private static T? FindDescendant<T>(DependencyObject source)
        where T : DependencyObject
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
}
