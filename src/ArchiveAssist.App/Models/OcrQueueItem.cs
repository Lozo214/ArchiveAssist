using ArchiveAssist.Core.Models;

namespace ArchiveAssist.App.Models;

public sealed record OcrQueueItem(
    ReportRow Source,
    string FileName,
    string RelativeFolder,
    string NonOcrPages,
    string OutputPath);
