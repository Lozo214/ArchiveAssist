using System.IO;
using System.Windows;
using ArchiveAssist.App.Services;

namespace ArchiveAssist.App;

public partial class RenameItemWindow : Window
{
    private readonly bool _isFolder;

    public RenameItemWindow(string currentName, bool isFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentName);
        _isFolder = isFolder;
        InitializeComponent();
        HeadingText.Text = isFolder ? "Rename folder" : "Rename file";
        GuidanceText.Text = isFolder
            ? "Enter a new name for this folder. Files and subfolders inside it will move with the folder."
            : "Enter the complete new filename. Include the extension if you want to keep it.";
        NewNameTextBox.Text = currentName;
        Loaded += RenameItemWindow_Loaded;
    }

    public string NewName => NewNameTextBox.Text;

    private void RenameItemWindow_Loaded(object sender, RoutedEventArgs e)
    {
        NewNameTextBox.Focus();
        var selectionLength = _isFolder
            ? NewNameTextBox.Text.Length
            : Math.Max(0, Path.GetFileNameWithoutExtension(NewNameTextBox.Text).Length);
        NewNameTextBox.Select(0, selectionLength);
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            FileSystemRenameService.ValidateName(NewNameTextBox.Text);
            DialogResult = true;
        }
        catch (ArgumentException exception)
        {
            ValidationText.Text = exception.Message;
            NewNameTextBox.Focus();
            NewNameTextBox.SelectAll();
        }
    }
}
