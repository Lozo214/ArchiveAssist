using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using ArchiveAssist.App.Models;
using Microsoft.Win32;

namespace ArchiveAssist.App;

public partial class ScanSelectionWindow : Window
{
    private string? _initialDirectory;
    private readonly bool _pdfOnly;

    public ScanSelectionWindow(
        IReadOnlyList<string> currentPaths,
        string? initialDirectory = null,
        bool pdfOnly = false)
    {
        InitializeComponent();
        _pdfOnly = pdfOnly;
        if (_pdfOnly)
        {
            Title = "Select PDFs and Folders - Archive Assist";
            HeadingText.Text = "Choose PDFs to process";
            DescriptionText.Text =
                "Add any combination of PDF files and folders. Selected folders are searched recursively, and duplicate PDFs are included only once.";
        }
        _initialDirectory = ResolveInitialDirectory(initialDirectory);
        DataContext = this;
        foreach (var path in currentPaths) AddPath(path);
        UpdateSelectionState();
    }

    public ObservableCollection<ScanSelectionItem> Selections { get; } = [];
    public IReadOnlyList<string> SelectedPaths { get; private set; } = [];

    private void AddFilesButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = _pdfOnly ? "Select PDF files to process" : "Select files to scan",
            Filter = _pdfOnly ? "PDF files (*.pdf)|*.pdf" : "All files (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true
        };
        if (Directory.Exists(_initialDirectory)) dialog.InitialDirectory = _initialDirectory;
        if (dialog.ShowDialog(this) != true) return;

        foreach (var path in dialog.FileNames) AddPath(path);
        _initialDirectory = Path.GetDirectoryName(dialog.FileNames.FirstOrDefault());
        UpdateSelectionState();
    }

    private void AddFoldersButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = _pdfOnly ? "Select folders containing PDFs" : "Select folders to scan recursively",
            Multiselect = true
        };
        if (Directory.Exists(_initialDirectory)) dialog.InitialDirectory = _initialDirectory;
        if (dialog.ShowDialog(this) != true) return;

        foreach (var path in dialog.FolderNames) AddPath(path);
        _initialDirectory = dialog.FolderNames.FirstOrDefault();
        UpdateSelectionState();
    }

    private void RemoveSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in SelectionGrid.SelectedItems.Cast<ScanSelectionItem>().ToList())
            Selections.Remove(item);
        UpdateSelectionState();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        Selections.Clear();
        UpdateSelectionState();
    }

    private void UseSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (Selections.Count == 0) return;
        SelectedPaths = Selections.Select(item => item.FullPath).ToList();
        DialogResult = true;
    }

    private void AddPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var fullPath = Path.GetFullPath(path);
        var isFolder = Directory.Exists(fullPath);
        if (!isFolder && !File.Exists(fullPath)) return;
        if (_pdfOnly && !isFolder &&
            !string.Equals(Path.GetExtension(fullPath), ".pdf", StringComparison.OrdinalIgnoreCase))
            return;
        if (Selections.Any(item => string.Equals(item.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))) return;
        Selections.Add(new(fullPath, isFolder));
    }

    private void UpdateSelectionState()
    {
        UseSelectionButton.IsEnabled = Selections.Count > 0;
        SelectionCountText.Text = Selections.Count switch
        {
            0 => "No files or folders selected",
            1 => "1 item selected",
            _ => $"{Selections.Count:N0} items selected"
        };
    }

    private static string? ResolveInitialDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (Directory.Exists(path)) return Path.GetFullPath(path);
        return File.Exists(path) ? Path.GetDirectoryName(Path.GetFullPath(path)) : null;
    }
}
