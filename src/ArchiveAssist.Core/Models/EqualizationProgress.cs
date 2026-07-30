namespace ArchiveAssist.Core.Models;

public sealed record EqualizationProgress(
    string Stage,
    string CurrentFileName,
    int CompletedFiles,
    int TotalFiles)
{
    public string Message => string.IsNullOrWhiteSpace(CurrentFileName)
        ? Stage
        : $"{Stage}: {CurrentFileName}";
}
