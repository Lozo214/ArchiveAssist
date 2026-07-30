using System.IO;
using ArchiveAssist.App.Services;

namespace ArchiveAssist.App.Tests;

public sealed class FileSystemRenameServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(),
        "ArchiveAssist.Rename.Tests",
        Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public void RenameFileMovesItWithoutChangingContents()
    {
        var source = Path.Combine(_root, "Original.pdf");
        File.WriteAllText(source, "contents");

        var renamed = FileSystemRenameService.Rename(source, "Renamed.pdf");

        Assert.False(File.Exists(source));
        Assert.Equal(Path.Combine(_root, "Renamed.pdf"), renamed);
        Assert.Equal("contents", File.ReadAllText(renamed));
    }

    [Fact]
    public void RenameFolderMovesItsCompleteContents()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "Old Folder")).FullName;
        File.WriteAllText(Path.Combine(folder, "inside.txt"), "contents");

        var renamed = FileSystemRenameService.Rename(folder, "New Folder");

        Assert.False(Directory.Exists(folder));
        Assert.True(Directory.Exists(renamed));
        Assert.Equal("contents", File.ReadAllText(Path.Combine(renamed, "inside.txt")));
    }

    [Fact]
    public void RenameRejectsCollisionsAndInvalidWindowsNames()
    {
        var source = Path.Combine(_root, "Source.pdf");
        var existing = Path.Combine(_root, "Existing.pdf");
        File.WriteAllText(source, "source");
        File.WriteAllText(existing, "existing");

        Assert.Throws<IOException>(() =>
            FileSystemRenameService.Rename(source, "Existing.pdf"));
        Assert.Throws<ArgumentException>(() =>
            FileSystemRenameService.ValidateName("CON.pdf"));
        Assert.Throws<ArgumentException>(() =>
            FileSystemRenameService.ValidateName("trailing."));
        Assert.True(File.Exists(source));
        Assert.Equal("existing", File.ReadAllText(existing));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
