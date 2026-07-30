using ArchiveAssist.Core.Models;

namespace ArchiveAssist.Core.Services;

public interface IFileRecoveryService
{
    string StorageRoot { get; }

    FileRecoveryPoint CreateRecoveryPoint(
        string sourcePath,
        string operation,
        int retentionDays);

    FileRecoveryPoint ReplaceFile(
        string verifiedReplacementPath,
        string sourcePath,
        string operation,
        int retentionDays);

    FileRecoveryRestoreResult Restore(string recoveryPointId, int retentionDays);

    IReadOnlyList<FileRecoveryPoint> GetRecoveryPoints(bool includeUnavailable = false);

    bool Delete(string recoveryPointId);

    int CleanupExpired(DateTimeOffset? currentTime = null);
}
