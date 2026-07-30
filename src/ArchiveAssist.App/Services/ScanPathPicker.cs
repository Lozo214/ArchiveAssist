using System.Windows;

namespace ArchiveAssist.App.Services;

public sealed class ScanPathPicker : IScanPathPicker
{
    public IReadOnlyList<string>? PickPaths(
        IReadOnlyList<string> currentPaths,
        string? initialDirectory = null)
    {
        var dialog = new ScanSelectionWindow(currentPaths, initialDirectory);
        var owner = Application.Current?.Windows.Cast<Window>().FirstOrDefault(window => window.IsActive);
        if (owner is not null) dialog.Owner = owner;
        return dialog.ShowDialog() == true ? dialog.SelectedPaths : null;
    }
}
