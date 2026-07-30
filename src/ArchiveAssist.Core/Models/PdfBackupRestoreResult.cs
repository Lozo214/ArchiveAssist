namespace ArchiveAssist.Core.Models;

public sealed record PdfBackupRestoreResult(
    string RestoredBackupPath,
    string PreservedEditedPath);
