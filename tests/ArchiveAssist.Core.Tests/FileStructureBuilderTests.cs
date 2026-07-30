using ArchiveAssist.Core.Models;
using ArchiveAssist.Core.Services;

namespace ArchiveAssist.Core.Tests;

public sealed class FileStructureBuilderTests
{
    [Fact]
    public void Build_RollsCountsAndWarningFilesUpThroughFolders()
    {
        var root = Path.GetFullPath("C:\\Archive");
        var rows = new[]
        {
            Row(Path.Combine(root, "Box 1", "Document.pdf"), ArchiveFileKind.Pdf,
                documents: 3, maps: 1, photos: 0, warning: "Page count exceeds limit"),
            Row(Path.Combine(root, "Box 1", "Photo.jpg"), ArchiveFileKind.Photo,
                documents: 0, maps: 0, photos: 1),
            Row(Path.Combine(root, "Box 2", "Card_back.tif"), ArchiveFileKind.PhotoBack,
                documents: 1, maps: 0, photos: 0),
            Row(Path.Combine(root, "notes.txt"), ArchiveFileKind.Skipped,
                documents: 0, maps: 0, photos: 0, warning: "Skipped: unsupported file type (.txt)")
        };

        var tree = FileStructureBuilder.Build(root, rows);

        Assert.Equal(4, tree.Documents);
        Assert.Equal(1, tree.Maps);
        Assert.Equal(1, tree.Photos);
        Assert.Equal(2, tree.WarningCount);
        Assert.Equal(3, tree.Children.Count);

        var box1 = Assert.Single(tree.Children, child => child.Name == "Box 1");
        Assert.True(box1.IsFolder);
        Assert.Equal(1, box1.Depth);
        Assert.Equal(19, box1.HierarchyIndent);
        Assert.Equal(3, box1.Documents);
        Assert.Equal(1, box1.Maps);
        Assert.Equal(1, box1.Photos);
        Assert.Equal(1, box1.WarningCount);
        Assert.Equal(2, box1.Children.Count);
        Assert.All(box1.Children, child => Assert.Equal(2, child.Depth));

        var skipped = Assert.Single(tree.Children, child => child.Name == "notes.txt");
        Assert.False(skipped.IsFolder);
        Assert.True(skipped.HasWarning);
    }

    private static ReportRow Row(
        string path,
        ArchiveFileKind kind,
        int documents,
        int maps,
        int photos,
        string? warning = null) =>
        new(
            Path.GetFileName(path),
            Path.GetRelativePath("C:\\Archive", Path.GetDirectoryName(path) ?? "C:\\Archive"),
            path,
            kind,
            100,
            kind == ArchiveFileKind.Pdf ? documents + maps : null,
            documents,
            maps,
            photos,
            kind == ArchiveFileKind.PhotoBack ? 1 : 0,
            null,
            false,
            warning,
            null);
}
