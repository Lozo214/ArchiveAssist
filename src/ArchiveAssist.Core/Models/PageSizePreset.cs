namespace ArchiveAssist.Core.Models;

public sealed record PageSizePreset(string Name, double Width, double Height, bool UseFeederRule = false)
{
    public const string StandardScannerName = "12 x 18 (Standard Scan Size)";

    public static IReadOnlyList<PageSizePreset> BuiltIn { get; } =
    [
        new("A4", 8.27, 11.69),
        new("Letter", 8.5, 11),
        new("Legal", 8.5, 14),
        new(StandardScannerName, 12, 18, UseFeederRule: true)
    ];

    public static PageSizePreset Default => BuiltIn.Single(preset => preset.Name == StandardScannerName);

    public bool IsMap(PageSize actual)
    {
        var normalized = actual.Normalized;
        if (UseFeederRule)
        {
            return normalized.Width > 12;
        }

        var limit = new PageSize(Width, Height).Normalized;
        return normalized.Width > limit.Width || normalized.Height > limit.Height;
    }

    public override string ToString() => Name;
}
