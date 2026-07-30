namespace ArchiveAssist.Core.Models;

public sealed class PdfEditWorkspaceSnapshot
{
    internal PdfEditWorkspaceSnapshot(byte[] pdfBytes, IReadOnlyList<PdfEditPage> pages)
    {
        PdfBytes = pdfBytes;
        Pages = pages;
    }

    internal byte[] PdfBytes { get; }

    internal IReadOnlyList<PdfEditPage> Pages { get; }

    public long ByteLength => PdfBytes.LongLength;
}
