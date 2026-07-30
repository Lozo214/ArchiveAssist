using ArchiveAssist.Core.Services;

namespace ArchiveAssist.Core.Tests;

public sealed class FileRecoveryServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(),
        "ArchiveAssist.Recovery.Tests",
        Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public void CreateRecoveryPointStoresCopyOutsideSourceFolder()
    {
        var source = WriteFile("Archive\\Document.pdf", "original");
        var service = CreateService();

        var point = service.CreateRecoveryPoint(source, "PDF Editor", 30);

        Assert.True(File.Exists(point.BackupPath));
        Assert.Equal("original", File.ReadAllText(point.BackupPath));
        Assert.StartsWith(Path.Combine(_root, "Managed Recovery"), point.BackupPath);
        Assert.DoesNotContain(
            Directory.GetFiles(Path.GetDirectoryName(source)!, "*", SearchOption.TopDirectoryOnly),
            path => !string.Equals(path, source, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(point, Assert.Single(service.GetRecoveryPoints()));
    }

    [Fact]
    public void ReplaceFileKeepsManagedRecoveryPointBeforeUpdatingOriginal()
    {
        var source = WriteFile("Archive\\Document.pdf", "original");
        var replacement = WriteFile("Archive\\replacement.part", "updated");
        var service = CreateService();

        var point = service.ReplaceFile(replacement, source, "PDF Optimization", 30);

        Assert.Equal("updated", File.ReadAllText(source));
        Assert.False(File.Exists(replacement));
        Assert.Equal("original", File.ReadAllText(point.BackupPath));
        Assert.Equal("PDF Optimization", point.Operation);
    }

    [Fact]
    public void RestorePreservesCurrentVersionAndRestoresSelectedPoint()
    {
        var source = WriteFile("Archive\\Document.pdf", "original");
        var replacement = WriteFile("Archive\\replacement.part", "updated");
        var service = CreateService();
        var originalPoint = service.ReplaceFile(
            replacement,
            source,
            "PDF Editor",
            30);

        var result = service.Restore(originalPoint.Id, 30);

        Assert.Equal("original", File.ReadAllText(source));
        Assert.NotNull(result.PreservedCurrentPoint);
        Assert.Equal("updated", File.ReadAllText(result.PreservedCurrentPoint!.BackupPath));
        Assert.Equal(2, service.GetRecoveryPoints().Count);
    }

    [Fact]
    public void CleanupExpiredRemovesOnlyExpiredRecoveryPoints()
    {
        var expiringSource = WriteFile("Archive\\Expiring.pdf", "one");
        var retainedSource = WriteFile("Archive\\Retained.pdf", "two");
        var service = CreateService();
        var expiring = service.CreateRecoveryPoint(expiringSource, "PDF Editor", 7);
        var retained = service.CreateRecoveryPoint(retainedSource, "PDF Editor", 0);

        var removed = service.CleanupExpired(DateTimeOffset.UtcNow.AddDays(8));

        Assert.Equal(1, removed);
        Assert.False(File.Exists(expiring.BackupPath));
        Assert.True(File.Exists(retained.BackupPath));
        Assert.Equal(retained.Id, Assert.Single(service.GetRecoveryPoints()).Id);
    }

    [Fact]
    public void DeleteRemovesRecoveryWithoutChangingCurrentFile()
    {
        var source = WriteFile("Archive\\Document.pdf", "current");
        var service = CreateService();
        var point = service.CreateRecoveryPoint(source, "PDF Editor", 30);

        Assert.True(service.Delete(point.Id));

        Assert.False(File.Exists(point.BackupPath));
        Assert.Empty(service.GetRecoveryPoints(includeUnavailable: true));
        Assert.Equal("current", File.ReadAllText(source));
    }

    [Fact]
    public void CorruptIndexPreventsNewRecoveryWithoutRemovingExistingFiles()
    {
        var firstSource = WriteFile("Archive\\First.pdf", "first");
        var secondSource = WriteFile("Archive\\Second.pdf", "second");
        var service = CreateService();
        var firstPoint = service.CreateRecoveryPoint(firstSource, "PDF Editor", 30);
        File.WriteAllText(
            Path.Combine(service.StorageRoot, "recovery-index.json"),
            "{ not valid json");

        var exception = Assert.Throws<InvalidDataException>(() =>
            service.CreateRecoveryPoint(secondSource, "PDF Editor", 30));

        Assert.Contains("No recovery files were changed", exception.Message);
        Assert.True(File.Exists(firstPoint.BackupPath));
        Assert.Single(Directory.GetFiles(
            Path.Combine(service.StorageRoot, "Files"),
            "*.pdf",
            SearchOption.TopDirectoryOnly));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private FileRecoveryService CreateService() =>
        new(Path.Combine(_root, "Managed Recovery"));

    private string WriteFile(string relativePath, string contents)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }
}
