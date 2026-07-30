using System.Text.Json;
using ArchiveAssist.Core.Models;

namespace ArchiveAssist.Core.Services;

public sealed class FileRecoveryService : IFileRecoveryService
{
    private readonly object _gate = new();
    private readonly string _filesDirectory;
    private readonly string _indexPath;

    public FileRecoveryService(string storageRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        StorageRoot = Path.GetFullPath(storageRoot);
        _filesDirectory = Path.Combine(StorageRoot, "Files");
        _indexPath = Path.Combine(StorageRoot, "recovery-index.json");
    }

    public string StorageRoot { get; }

    public static FileRecoveryService CreateDefault() =>
        new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArchiveAssist",
            "Recovery"));

    public FileRecoveryPoint CreateRecoveryPoint(
        string sourcePath,
        string operation,
        int retentionDays)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        var fullSourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSourcePath))
        {
            throw new FileNotFoundException(
                "The file is no longer available, so a recovery point could not be created.",
                fullSourcePath);
        }

        lock (_gate)
        {
            Directory.CreateDirectory(_filesDirectory);
            var id = Guid.NewGuid().ToString("N");
            var extension = Path.GetExtension(fullSourcePath);
            var backupPath = Path.Combine(_filesDirectory, $"{id}{extension}");
            var temporaryPath = backupPath + ".part";
            var createdAt = DateTimeOffset.UtcNow;
            var point = new FileRecoveryPoint(
                id,
                fullSourcePath,
                backupPath,
                operation.Trim(),
                createdAt,
                retentionDays > 0 ? createdAt.AddDays(retentionDays) : null,
                new FileInfo(fullSourcePath).Length);

            try
            {
                File.Copy(fullSourcePath, temporaryPath, overwrite: false);
                File.Move(temporaryPath, backupPath, overwrite: false);
                var points = LoadIndex();
                points.Add(point);
                SaveIndex(points);
                return point;
            }
            catch
            {
                TryDeleteFile(temporaryPath);
                TryDeleteFile(backupPath);
                throw;
            }
        }
    }

    public FileRecoveryPoint ReplaceFile(
        string verifiedReplacementPath,
        string sourcePath,
        string operation,
        int retentionDays)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verifiedReplacementPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fullReplacementPath = Path.GetFullPath(verifiedReplacementPath);
        var fullSourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullReplacementPath))
        {
            throw new FileNotFoundException(
                "The verified replacement file is no longer available.",
                fullReplacementPath);
        }

        var recoveryPoint = CreateRecoveryPoint(fullSourcePath, operation, retentionDays);
        try
        {
            ReplaceWithoutCreatingRecoveryPoint(fullReplacementPath, fullSourcePath);
            return recoveryPoint;
        }
        catch
        {
            Delete(recoveryPoint.Id);
            throw;
        }
    }

    public FileRecoveryRestoreResult Restore(string recoveryPointId, int retentionDays)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryPointId);
        FileRecoveryPoint point;
        lock (_gate)
        {
            point = LoadIndex().FirstOrDefault(candidate =>
                        string.Equals(candidate.Id, recoveryPointId, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("That recovery point is no longer available.");
        }

        if (!File.Exists(point.BackupPath))
        {
            throw new FileNotFoundException(
                "The recovery file is missing and cannot be restored.",
                point.BackupPath);
        }

        var originalDirectory = Path.GetDirectoryName(point.OriginalPath)
            ?? throw new InvalidOperationException("The original file path does not have a parent folder.");
        Directory.CreateDirectory(originalDirectory);
        var temporaryPath = Path.Combine(
            originalDirectory,
            $".{Path.GetFileName(point.OriginalPath)}.{Guid.NewGuid():N}.archiveassist.restore.part");
        FileRecoveryPoint? preservedCurrentPoint = null;

        try
        {
            File.Copy(point.BackupPath, temporaryPath, overwrite: false);
            if (File.Exists(point.OriginalPath))
            {
                preservedCurrentPoint = CreateRecoveryPoint(
                    point.OriginalPath,
                    $"Before restoring {point.Operation}",
                    retentionDays);
                ReplaceWithoutCreatingRecoveryPoint(temporaryPath, point.OriginalPath);
            }
            else
            {
                File.Move(temporaryPath, point.OriginalPath, overwrite: false);
            }

            return new(point, preservedCurrentPoint, point.OriginalPath);
        }
        catch
        {
            if (preservedCurrentPoint is not null)
            {
                Delete(preservedCurrentPoint.Id);
            }

            throw;
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    public IReadOnlyList<FileRecoveryPoint> GetRecoveryPoints(bool includeUnavailable = false)
    {
        lock (_gate)
        {
            return LoadIndex()
                .Where(point => includeUnavailable || point.IsAvailable)
                .OrderByDescending(point => point.CreatedAtUtc)
                .ToList();
        }
    }

    public bool Delete(string recoveryPointId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryPointId);
        lock (_gate)
        {
            var points = LoadIndex();
            var point = points.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, recoveryPointId, StringComparison.OrdinalIgnoreCase));
            if (point is null)
            {
                return false;
            }

            TryDeleteFile(point.BackupPath);
            points.Remove(point);
            SaveIndex(points);
            return true;
        }
    }

    public int CleanupExpired(DateTimeOffset? currentTime = null)
    {
        var now = currentTime ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            var points = LoadIndex();
            var expired = points
                .Where(point => point.ExpiresAtUtc is { } expires && expires <= now)
                .ToList();
            foreach (var point in expired)
            {
                TryDeleteFile(point.BackupPath);
                points.Remove(point);
            }

            if (expired.Count > 0)
            {
                SaveIndex(points);
            }

            return expired.Count;
        }
    }

    private void ReplaceWithoutCreatingRecoveryPoint(
        string replacementPath,
        string sourcePath)
    {
        var rollbackPath = Path.Combine(
            Path.GetDirectoryName(sourcePath)
                ?? throw new InvalidOperationException("The original path does not have a parent folder."),
            $".{Path.GetFileName(sourcePath)}.{Guid.NewGuid():N}.archiveassist.rollback");
        try
        {
            File.Replace(replacementPath, sourcePath, rollbackPath, ignoreMetadataErrors: true);
            TryDeleteFile(rollbackPath);
        }
        catch
        {
            if (!File.Exists(sourcePath) && File.Exists(rollbackPath))
            {
                File.Move(rollbackPath, sourcePath, overwrite: false);
            }

            throw;
        }
        finally
        {
            if (File.Exists(sourcePath))
            {
                TryDeleteFile(rollbackPath);
            }
        }
    }

    private List<FileRecoveryPoint> LoadIndex()
    {
        try
        {
            if (!File.Exists(_indexPath))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<FileRecoveryPoint>>(
                       File.ReadAllText(_indexPath))
                   ?? [];
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Archive Assist could not read the Recovery Center index. No recovery files were changed.",
                exception);
        }
    }

    private void SaveIndex(IReadOnlyCollection<FileRecoveryPoint> points)
    {
        Directory.CreateDirectory(StorageRoot);
        var temporaryPath = _indexPath + $".{Guid.NewGuid():N}.part";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(points, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, _indexPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Cleanup failures are non-fatal. The index remains the source of truth.
        }
    }
}
