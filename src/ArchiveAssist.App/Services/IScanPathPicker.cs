namespace ArchiveAssist.App.Services;

public interface IScanPathPicker
{
    IReadOnlyList<string>? PickPaths(
        IReadOnlyList<string> currentPaths,
        string? initialDirectory = null);
}
