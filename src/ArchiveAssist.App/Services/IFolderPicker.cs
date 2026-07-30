namespace ArchiveAssist.App.Services;

public interface IFolderPicker
{
    string? PickFolder(string? initialFolder = null, string? title = null);
}
