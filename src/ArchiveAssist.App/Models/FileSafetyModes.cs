namespace ArchiveAssist.App.Models;

public static class FileSafetyModes
{
    public const string SafeInPlace = "SafeInPlace";
    public const string AlwaysAsk = "AlwaysAsk";
    public const string SaveCopies = "SaveCopies";

    public static string Normalize(string? value) =>
        value is AlwaysAsk or SaveCopies ? value : SafeInPlace;
}
