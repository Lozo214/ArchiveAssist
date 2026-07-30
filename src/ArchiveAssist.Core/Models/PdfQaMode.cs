namespace ArchiveAssist.Core.Models;

public sealed record PdfQaMode(string Name, string Description)
{
    public static PdfQaMode FastCountOnly { get; } = new(
        "Fast Count Only",
        "Counts and page-size checks only; searchable-text checks are skipped.");

    public static PdfQaMode StandardQa { get; } = new(
        "Standard QA",
        "Samples a spread of pages for a quick searchable-text estimate.");

    public static PdfQaMode DeepOcrCheck { get; } = new(
        "Deep OCR Check",
        "Checks every page and reports exact page numbers without searchable text.");

    public static IReadOnlyList<PdfQaMode> BuiltIn { get; } =
    [
        FastCountOnly,
        StandardQa,
        DeepOcrCheck
    ];

    public override string ToString() => Name;
}
