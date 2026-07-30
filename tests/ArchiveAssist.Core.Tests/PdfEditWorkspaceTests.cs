using ArchiveAssist.Core.Services;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace ArchiveAssist.Core.Tests;

public sealed class PdfEditWorkspaceTests : IDisposable
{
    private readonly string _testRoot = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(),
        "ArchiveAssist.Editor.Tests",
        Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public void OpenBuildsPageMetadata()
    {
        var source = WritePdf("source.pdf", 3);
        using var workspace = new PdfEditWorkspace();

        workspace.Open(source);

        Assert.True(workspace.HasDocument);
        Assert.Equal(Path.GetFullPath(source), workspace.SourcePath);
        Assert.Equal([1, 2, 3], workspace.Pages.Select(page => page.PageNumber));
        Assert.Equal([0, 1, 2], workspace.Pages.Select(page => page.PageIndex));
    }

    [Fact]
    public void RotateCropAndDeleteUpdateWorkingCopy()
    {
        var source = WritePdf("source.pdf", 3);
        using var workspace = new PdfEditWorkspace();
        workspace.Open(source);

        workspace.RotatePages([0, 2], 90);
        workspace.CropPages([0], left: 10, top: 20, right: 30, bottom: 40);
        workspace.DeletePages([1]);

        Assert.Equal(2, workspace.Pages.Count);
        Assert.Equal([90, 90], workspace.Pages.Select(page => page.RotationDegrees));
        Assert.True(workspace.Pages[0].IsCropped);
        Assert.Equal([1, 2], workspace.Pages.Select(page => page.PageNumber));

        using var edited = PdfReader.Open(
            new MemoryStream(workspace.GetPdfBytesSnapshot()),
            PdfDocumentOpenMode.Import);
        Assert.Equal(2, edited.PageCount);
        Assert.Equal(90, edited.Pages[0].Rotate);
        Assert.Equal(560, edited.Pages[0].EffectiveCropBoxReadOnly.Width, precision: 3);
        Assert.Equal(740, edited.Pages[0].EffectiveCropBoxReadOnly.Height, precision: 3);
    }

    [Fact]
    public void DeleteCannotRemoveEveryPage()
    {
        var source = WritePdf("source.pdf", 2);
        using var workspace = new PdfEditWorkspace();
        workspace.Open(source);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            workspace.DeletePages([0, 1]));

        Assert.Contains("at least one page", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, workspace.Pages.Count);
    }

    [Fact]
    public void ReorderPagesMovesAGroupAndPreservesItsRelativeOrder()
    {
        var source = WritePdfWithPageWidths("source.pdf", [600, 610, 620, 630]);
        using var workspace = new PdfEditWorkspace();
        workspace.Open(source);
        var originalIds = workspace.Pages.Select(page => page.Id).ToList();

        var changed = workspace.ReorderPages([1, 2], insertionIndex: 0);

        Assert.True(changed);
        Assert.Equal(
            [originalIds[1], originalIds[2], originalIds[0], originalIds[3]],
            workspace.Pages.Select(page => page.Id));
        Assert.Equal([1, 2, 3, 4], workspace.Pages.Select(page => page.PageNumber));
        using var reordered = PdfReader.Open(
            new MemoryStream(workspace.GetPdfBytesSnapshot()),
            PdfDocumentOpenMode.Import);
        Assert.Equal(
            [610d, 620d, 600d, 630d],
            Enumerable.Range(0, reordered.PageCount)
                .Select(index => reordered.Pages[index].Width.Point));
    }

    [Fact]
    public void ReorderPagesReturnsFalseWhenTheOrderDoesNotChange()
    {
        var source = WritePdf("source.pdf", 3);
        using var workspace = new PdfEditWorkspace();
        workspace.Open(source);
        var originalBytes = workspace.GetPdfBytesSnapshot();

        var changed = workspace.ReorderPages([0, 1], insertionIndex: 0);

        Assert.False(changed);
        Assert.Equal(originalBytes, workspace.GetPdfBytesSnapshot());
    }

    [Fact]
    public void SnapshotRestoresPagesAndPdfBytes()
    {
        var source = WritePdf("source.pdf", 3);
        using var workspace = new PdfEditWorkspace();
        workspace.Open(source);
        var snapshot = workspace.CreateSnapshot();

        workspace.RotatePages([0], 90);
        workspace.DeletePages([1]);
        workspace.RestoreSnapshot(snapshot);

        Assert.Equal(3, workspace.Pages.Count);
        Assert.All(workspace.Pages, page => Assert.Equal(0, page.RotationDegrees));
        using var restored = PdfReader.Open(
            new MemoryStream(workspace.GetPdfBytesSnapshot()),
            PdfDocumentOpenMode.Import);
        Assert.Equal(3, restored.PageCount);
        Assert.Equal(0, restored.Pages[0].Rotate);
    }

    [Fact]
    public void SaveCopyPreservesSourceAndRejectsSourcePath()
    {
        var source = WritePdf("source.pdf", 2);
        var originalBytes = File.ReadAllBytes(source);
        var output = Path.Combine(_testRoot, "edited.pdf");
        using var workspace = new PdfEditWorkspace();
        workspace.Open(source);
        workspace.RotatePages([0], 90);

        workspace.SaveCopy(output);

        Assert.Equal(originalBytes, File.ReadAllBytes(source));
        Assert.Equal(90, ReadRotation(output, 0));
        Assert.Throws<InvalidOperationException>(() => workspace.SaveCopy(source));
        Assert.Equal(originalBytes, File.ReadAllBytes(source));
    }

    [Fact]
    public void SaveReplacesSourceAndKeepsOneOriginalBackupPerSession()
    {
        var source = WritePdf("source.pdf", 2);
        var originalBytes = File.ReadAllBytes(source);
        var recoveryService = new FileRecoveryService(Path.Combine(_testRoot, "Recovery"));
        using var workspace = new PdfEditWorkspace(recoveryService);
        workspace.Open(source);
        workspace.RotatePages([0], 90);

        var firstBackup = workspace.Save();

        Assert.True(File.Exists(firstBackup));
        Assert.Equal(originalBytes, File.ReadAllBytes(firstBackup));
        Assert.Equal(90, ReadRotation(source, 0));
        Assert.Equal(firstBackup, workspace.SessionBackupPath);

        workspace.RotatePages([1], 90);
        var secondBackup = workspace.Save();

        Assert.Equal(firstBackup, secondBackup);
        Assert.Equal(originalBytes, File.ReadAllBytes(firstBackup));
        Assert.Equal([90, 90], new[] { ReadRotation(source, 0), ReadRotation(source, 1) });
        Assert.Single(recoveryService.GetRecoveryPoints());
    }

    [Fact]
    public void RestoreSessionBackupPreservesEditedVersionBeforeRestoringOriginal()
    {
        var source = WritePdf("source.pdf", 2);
        var recoveryService = new FileRecoveryService(Path.Combine(_testRoot, "Recovery"));
        using var workspace = new PdfEditWorkspace(recoveryService);
        workspace.Open(source);
        workspace.RotatePages([0], 90);
        var originalBackup = workspace.Save();

        var result = workspace.RestoreSessionBackup();

        Assert.Equal(originalBackup, result.RestoredBackupPath);
        Assert.Equal(0, ReadRotation(source, 0));
        Assert.Equal(90, ReadRotation(result.PreservedEditedPath, 0));
        Assert.True(File.Exists(result.PreservedEditedPath));
        Assert.Equal(originalBackup, workspace.SessionBackupPath);
        Assert.Equal(2, recoveryService.GetRecoveryPoints().Count);
        Assert.All(workspace.Pages, page => Assert.Equal(0, page.RotationDegrees));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private string WritePdf(string fileName, int pageCount)
    {
        var path = Path.Combine(_testRoot, fileName);
        using var document = new PdfDocument();

        for (var index = 0; index < pageCount; index++)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(600);
            page.Height = XUnit.FromPoint(800);
        }

        document.Save(path);
        return path;
    }

    private string WritePdfWithPageWidths(string fileName, IReadOnlyList<double> widths)
    {
        var path = Path.Combine(_testRoot, fileName);
        using var document = new PdfDocument();

        foreach (var width in widths)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(width);
            page.Height = XUnit.FromPoint(800);
        }

        document.Save(path);
        return path;
    }

    private static int ReadRotation(string path, int pageIndex)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return document.Pages[pageIndex].Rotate;
    }
}
