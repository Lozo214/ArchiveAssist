namespace ArchiveAssist.Core.Models;

public sealed record DetailField(string Field, string Value);

public sealed record PdfMetadataDetails(IReadOnlyList<DetailField> Fields);
