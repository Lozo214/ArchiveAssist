namespace ArchiveAssist.Core.Models;

public sealed record FileRecoveryPoint(
    string Id,
    string OriginalPath,
    string BackupPath,
    string Operation,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    long OriginalBytes)
{
    public string FileName => Path.GetFileName(OriginalPath);

    public string Folder => Path.GetDirectoryName(OriginalPath) ?? string.Empty;

    public string CreatedLabel => CreatedAtUtc.LocalDateTime.ToString("g");

    public string ExpiresLabel => ExpiresAtUtc is { } expires
        ? expires.LocalDateTime.ToString("g")
        : "Kept until deleted";

    public string SizeLabel => OriginalBytes >= 1024 * 1024
        ? $"{OriginalBytes / (1024d * 1024d):N1} MB"
        : $"{OriginalBytes / 1024d:N1} KB";

    public bool IsAvailable => File.Exists(BackupPath);

    public string StatusLabel => IsAvailable ? "Ready" : "Recovery file missing";
}

public sealed record FileRecoveryRestoreResult(
    FileRecoveryPoint RestoredPoint,
    FileRecoveryPoint? PreservedCurrentPoint,
    string RestoredPath);
