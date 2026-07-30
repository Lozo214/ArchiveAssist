using System.IO;
using ArchiveAssist.App.Models;

namespace ArchiveAssist.App.Services;

public static class RecentPdfService
{
    public const int MaximumRecentPdfs = 8;

    public static bool Add(AppSettings settings, string pdfPath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        var fullPath = Path.GetFullPath(pdfPath);
        var original = settings.RecentPdfPaths ?? [];
        var updated = original
            .Where(path => !string.Equals(path, fullPath, StringComparison.OrdinalIgnoreCase))
            .Prepend(fullPath)
            .Take(MaximumRecentPdfs)
            .ToList();
        var changed = !original.SequenceEqual(updated, StringComparer.OrdinalIgnoreCase);
        settings.RecentPdfPaths = updated;
        return changed;
    }

    public static bool PruneUnavailable(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var original = settings.RecentPdfPaths ?? [];
        var available = original
            .Where(IsAvailablePdf)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumRecentPdfs)
            .ToList();
        var changed = !original.SequenceEqual(available, StringComparer.OrdinalIgnoreCase);
        settings.RecentPdfPaths = available;
        return changed;
    }

    public static IReadOnlyList<string> Available(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return (settings.RecentPdfPaths ?? [])
            .Where(IsAvailablePdf)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumRecentPdfs)
            .ToList();
    }

    private static bool IsAvailablePdf(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase) &&
        File.Exists(path);
}
