using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using MdModManager.Helpers;
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

        // 旧下载源迁移：github.com / kkgithub.com / ghproxy.net 等已废弃，统一迁移到 suzimo + 高速DNS
        var ds = config.DownloadSource;
        if (string.IsNullOrWhiteSpace(ds) ||
            ds.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            ds.Equals("kkgithub.com", StringComparison.OrdinalIgnoreCase) ||
            ds.Equals("ghproxy.net", StringComparison.OrdinalIgnoreCase))
        {
            config.DownloadSource = MirrorDomainRegistry.SuzimoAlias;
            config.UseOptimizedDns = true;
        }
        else if (MirrorDomainRegistry.IsSuzimoDownloadSource(ds))
        {
            config.DownloadSource = MirrorDomainRegistry.SuzimoAlias;
        }
    }
}
