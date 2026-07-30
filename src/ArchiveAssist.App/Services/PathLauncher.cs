using System.Diagnostics;

namespace ArchiveAssist.App.Services;

public static class PathLauncher
{
    public static void Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public static void ShowInFolder(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{path}\"",
            UseShellExecute = true
        });
    }
}
