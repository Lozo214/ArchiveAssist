namespace ArchiveAssist.App.Models;

public sealed class PdfRescanRequestedEventArgs : EventArgs
{
    public bool ScanStarted { get; set; }
}
