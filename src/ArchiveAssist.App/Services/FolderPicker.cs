using System.IO;
using Microsoft.Win32;

namespace ArchiveAssist.App.Services;

public sealed class FolderPicker : IFolderPicker
{
    public string? PickFolder(string? initialFolder = null, string? title = null)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title ?? "Select an archive folder",
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(initialFolder) && Directory.Exists(initialFolder))
        {
            dialog.InitialDirectory = initialFolder;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
