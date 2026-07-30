using ArchiveAssist.Core.Models;

namespace ArchiveAssist.Core.Services;

public interface IPdfMetadataReader
{
    PdfMetadataDetails Read(string path);
}
