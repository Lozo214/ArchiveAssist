namespace ArchiveAssist.Core.Models;

public sealed record PdfEditPage(
    Guid Id,
    int PageIndex,
    int PageNumber,
    int RotationDegrees,
    bool IsCropped);
