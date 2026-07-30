using ArchiveAssist.Core.Models;

namespace ArchiveAssist.App.Models;

public sealed class ScanCompletedEventArgs(PdfScanSummary summary) : EventArgs
{
    public PdfScanSummary Summary { get; } = summary;
}
