namespace ArchiveAssist.Core.Models;

public sealed record EqualizationFolderPlan(
    string SourceFolder,
    string RelativeFolder,
    int SourceFiles,
    int SourcePages,
    int FilesOverLimit,
    int ExpectedOutputFiles,
    IReadOnlyList<string> SourcePdfPaths)
{
    public bool NeedsWork => FilesOverLimit > 0;
    public string ActionLabel => NeedsWork ? "Will equalize" : "No change";
}
