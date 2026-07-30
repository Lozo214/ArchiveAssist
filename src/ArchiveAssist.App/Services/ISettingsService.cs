using ArchiveAssist.App.Models;

namespace ArchiveAssist.App.Services;

public interface ISettingsService
{
    AppSettings Load();
    void Save(AppSettings settings);
}
