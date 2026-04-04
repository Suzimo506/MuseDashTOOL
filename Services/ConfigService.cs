using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using MdModManager.Models;

namespace MdModManager.Services;

public interface IConfigService
{
    AppConfig Config { get; }
    void Load();
    Task LoadAsync();
    Task SaveAsync();
}

public class ConfigService : IConfigService
{
    private readonly string _configFolderPath;
    private readonly string _configFilePath;

    public AppConfig Config { get; private set; } = new AppConfig();

    public ConfigService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _configFolderPath = Path.Combine(appData, "MdModManager");
        _configFilePath = Path.Combine(_configFolderPath, "config.json");
    }

    public void Load()
    {
        try
        {
            if (File.Exists(_configFilePath))
            {
                var json = File.ReadAllText(_configFilePath);
                var config = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppConfig);
                if (config != null)
                {
                    NormalizeLegacyConfig(config);
                    Config = config;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load config: {ex}");
        }
    }

    public async Task LoadAsync()
    {
        try
        {
            if (File.Exists(_configFilePath))
            {
                var json = await File.ReadAllTextAsync(_configFilePath);
                var config = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppConfig);
                if (config != null)
                {
                    NormalizeLegacyConfig(config);
                    Config = config;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load config: {ex}");
        }
    }

    public async Task SaveAsync()
    {
        try
        {
            if (!Directory.Exists(_configFolderPath))
            {
                Directory.CreateDirectory(_configFolderPath);
            }

            var json = JsonSerializer.Serialize(Config, AppJsonContext.Default.AppConfig);
            await File.WriteAllTextAsync(_configFilePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save config: {ex}");
        }
    }

    private static void NormalizeLegacyConfig(AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.ModLinksUrl) &&
            config.ModLinksUrl.Contains("raw.githubusercontent.com/MDMods/MuseDashModLinks/main/ModLinks.json"))
        {
            config.ModLinksUrl = "https://gitee.com/lxymahatma/ModLinks/raw/dev/Mods.json";
        }

        if (!string.IsNullOrWhiteSpace(config.DownloadSource) &&
            config.DownloadSource.Contains("suzimo.online", StringComparison.OrdinalIgnoreCase))
        {
            config.DownloadSource = "suzimo.site";
        }
    }
}
