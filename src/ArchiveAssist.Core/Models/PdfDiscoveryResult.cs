namespace ArchiveAssist.Core.Models;

public sealed record DiscoveredFile(
    string FullPath,
    string FileName,
    string RelativeFolder,
    string Extension,
    ArchiveFileKind Kind)
{
    public string TypeLabel => Kind switch
    {
        ArchiveFileKind.Pdf => "PDF",
        ArchiveFileKind.Photo => "Photo",
        ArchiveFileKind.PhotoBack => "Photo Back",
        _ => "Skipped"
    };
}

public sealed record PdfDiscoveryResult(
    string RootPath,
    IReadOnlyList<DiscoveredFile> Files,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string>? InputPaths = null)
{
    public IReadOnlyList<string> SelectedPaths => InputPaths ?? [RootPath];
    public int PdfCount => Files.Count(file => file.Kind == ArchiveFileKind.Pdf);
    public int PhotoCount => Files.Count(file => file.Kind == ArchiveFileKind.Photo);
    public int PhotoBackCount => Files.Count(file => file.Kind == ArchiveFileKind.PhotoBack);
    public int SkippedCount => Files.Count(file => file.Kind == ArchiveFileKind.Skipped);
}
