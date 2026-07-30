using System.IO;

namespace ArchiveAssist.App.Models;

public sealed record ScanSelectionItem(string FullPath, bool IsFolder)
{
    public string Name => IsFolder
        ? Path.GetFileName(Path.TrimEndingDirectorySeparator(FullPath))
        : Path.GetFileName(FullPath);

    public string TypeLabel => IsFolder ? "Folder" : "File";
}
