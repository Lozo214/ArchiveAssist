using ArchiveAssist.Core.Models;

namespace ArchiveAssist.App.Models;

public sealed record OcrPageModeOption(
    PdfOcrPageMode Mode,
    string Name,
    string Description);
