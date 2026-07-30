using ArchiveAssist.Core.Models;

namespace ArchiveAssist.App.Models;

public sealed record PdfOptimizationOption(
    PdfOptimizationLevel Level,
    string Name,
    string Description);
