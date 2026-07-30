using System.IO;
using ArchiveAssist.App.Models;

namespace ArchiveAssist.App.Services;

public static class RecentScanLocationService
{
    public const int MaximumRecentLocations = 8;

    public static bool AddRange(AppSettings settings, IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(paths);
        var original = settings.RecentScanPaths ?? [];
        var updated = original.ToList();

        foreach (var path in paths
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Select(Path.GetFullPath)
                     .Reverse())
        {
            updated.RemoveAll(existing =>
                string.Equals(existing, path, StringComparison.OrdinalIgnoreCase));
            updated.Insert(0, path);
        }

        updated = updated
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumRecentLocations)
            .ToList();
        var changed = !original.SequenceEqual(updated, StringComparer.OrdinalIgnoreCase);
        settings.RecentScanPaths = updated;
        return changed;
    }

    public static bool PruneUnavailable(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var original = settings.RecentScanPaths ?? [];
        var available = original
            .Where(path => !string.IsNullOrWhiteSpace(path) &&
                           (File.Exists(path) || Directory.Exists(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumRecentLocations)
            .ToList();
        var changed = !original.SequenceEqual(available, StringComparer.OrdinalIgnoreCase);
        settings.RecentScanPaths = available;
        return changed;
    }

    public static IReadOnlyList<string> Available(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return (settings.RecentScanPaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path) &&
                           (File.Exists(path) || Directory.Exists(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumRecentLocations)
            .ToList();
    }
}
