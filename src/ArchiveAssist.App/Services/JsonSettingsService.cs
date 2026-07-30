using System.IO;
using System.Text.Json;
using ArchiveAssist.App.Models;

namespace ArchiveAssist.App.Services;

public sealed class JsonSettingsService(string? settingsPath = null) : ISettingsService
{
    private readonly string _settingsPath = settingsPath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ArchiveAssist",
        "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return new();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath)) ?? new();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var folder = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Preferences should never prevent the main scanning workflow.
        }
    }
}
