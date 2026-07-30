namespace ArchiveAssist.Core.Models;

public sealed record EqualizationResult(
    string OutputRoot,
    IReadOnlyList<string> OutputPdfPaths,
    string? ManifestPath)
{
    public int OutputFiles => OutputPdfPaths.Count;
}
