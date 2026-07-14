using System.IO;
using System.Text.Json;
using Chat2ApiTray.Models;

namespace Chat2ApiTray.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public SettingsService(string dataDirectory)
    {
        LocalDataDirectorySecurity.EnsurePrivateDirectory(dataDirectory);
        SettingsPath = Path.Combine(dataDirectory, "tray-settings.json");
    }

    public string SettingsPath { get; }

    public async Task<TraySettings> LoadAsync()
    {
        if (!File.Exists(SettingsPath))
        {
            var defaults = new TraySettings
            {
                ProjectDirectory = FindProjectDirectory()
            };
            await SaveAsync(defaults);
            return defaults;
        }

        await using var stream = File.OpenRead(SettingsPath);
        var settings = await JsonSerializer.DeserializeAsync<TraySettings>(stream, JsonOptions) ?? new TraySettings();
        if (string.IsNullOrWhiteSpace(settings.ProjectDirectory))
        {
            settings.ProjectDirectory = FindProjectDirectory();
        }

        return settings;
    }

    public async Task SaveAsync(TraySettings settings)
    {
        LocalDataDirectorySecurity.EnsurePrivateDirectory(Path.GetDirectoryName(SettingsPath) ?? AppContext.BaseDirectory);
        await using var stream = File.Create(SettingsPath);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions);
    }

    private static string FindProjectDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var packagePath = Path.Combine(directory.FullName, "package.json");
            var distPath = Path.Combine(directory.FullName, "dist", "index.js");
            if (File.Exists(packagePath) && File.Exists(distPath))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return AppContext.BaseDirectory;
    }
}
