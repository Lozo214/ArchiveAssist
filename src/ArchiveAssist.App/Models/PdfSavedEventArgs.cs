namespace ArchiveAssist.App.Models;

public sealed class PdfSavedEventArgs(
    string pdfPath,
    string backupPath,
    bool wasBackupRestore = false,
    string? preservedEditedPath = null,
    bool wasSavedAsCopy = false) : EventArgs
{
    public string PdfPath { get; } = pdfPath;

    public string BackupPath { get; } = backupPath;

    public bool WasBackupRestore { get; } = wasBackupRestore;

    public string? PreservedEditedPath { get; } = preservedEditedPath;

    public bool WasSavedAsCopy { get; } = wasSavedAsCopy;
}
