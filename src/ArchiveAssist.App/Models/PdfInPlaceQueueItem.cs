using System.IO;

namespace ArchiveAssist.App.Models;

public sealed record PdfInPlaceQueueItem(string FullPath)
{
    public string FileName => Path.GetFileName(FullPath);
    public string Folder => Path.GetDirectoryName(FullPath) ?? string.Empty;
    public long SizeBytes => File.Exists(FullPath) ? new FileInfo(FullPath).Length : 0;
    public string SizeLabel => FormatBytes(SizeBytes);

    private static string FormatBytes(long bytes) =>
        bytes >= 1024 * 1024
            ? $"{bytes / (1024d * 1024d):N1} MB"
            : $"{bytes / 1024d:N1} KB";
}
