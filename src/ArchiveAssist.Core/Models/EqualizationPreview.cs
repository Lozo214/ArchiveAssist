namespace ArchiveAssist.Core.Models;

public sealed record EqualizationPreview(
    string RootFolder,
    int MaxPagesPerPdf,
    IReadOnlyList<EqualizationFolderPlan> Folders)
{
    public int SourceFiles => Folders.Sum(folder => folder.SourceFiles);
    public int SourcePages => Folders.Sum(folder => folder.SourcePages);
    public int FilesOverLimit => Folders.Sum(folder => folder.FilesOverLimit);
    public int ExpectedOutputFiles => Folders.Sum(folder => folder.ExpectedOutputFiles);
    public int FoldersRequiringWork => Folders.Count(folder => folder.NeedsWork);
    public bool HasWork => FilesOverLimit > 0;
}
