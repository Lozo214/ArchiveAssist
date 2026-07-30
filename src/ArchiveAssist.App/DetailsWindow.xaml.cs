using System.Windows;
using ArchiveAssist.App.Services;
using ArchiveAssist.App.ViewModels;
using ArchiveAssist.Core.Models;
using ArchiveAssist.Core.Services;

namespace ArchiveAssist.App;

public partial class DetailsWindow : Window
{
    private readonly ReportRow _row;

    public DetailsWindow(ReportRow row, IPdfMetadataReader metadataReader)
    {
        _row = row;
        InitializeComponent();
        Title = $"File Details - {row.FileName}";
        DataContext = new FileDetailsViewModel(row, metadataReader);
    }

    private void OpenFileButton_Click(object sender, RoutedEventArgs e) =>
        RunPathAction(() => PathLauncher.Open(_row.FullPath));

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e) =>
        RunPathAction(() => PathLauncher.ShowInFolder(_row.FullPath));

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void RunPathAction(Action action)
    {
        try { action(); }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Could not open path", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
