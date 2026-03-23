using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using MdModManager.Models;

namespace MdModManager.Services;

public interface IAnnouncementService
{
    Task<NoticeInfo?> GetLatestAnnouncementAsync();
}

public class AnnouncementService : IAnnouncementService
{
    private const string AnnouncementUrl = "https://raw.githubusercontent.com/KuoKing506/-MuseDashTOOL/refs/heads/main/announcement.json";
    private readonly HttpClient _httpClient;

    public AnnouncementService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "MuseDashModTool-Announcement");
        // 设置较短的超时时间，避免网络环境差时卡顿
        _httpClient.Timeout = TimeSpan.FromSeconds(5);
    }

    public async Task<NoticeInfo?> GetLatestAnnouncementAsync()
    {
        try
        {
            // 优先检查本地是否存在 announcement.json (方便本地预览测试)
            string[] possiblePaths = {
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "announcement.json"),
                "announcement.json",
                System.IO.Path.Combine(Environment.CurrentDirectory, "announcement.json")
            };

            string? localPath = null;
            foreach (var path in possiblePaths)
            {
                if (System.IO.File.Exists(path))
                {
                    localPath = path;
                    break;
                }
            }

            if (localPath != null)
            {
                System.Diagnostics.Debug.WriteLine($"Loading local announcement from: {System.IO.Path.GetFullPath(localPath)}");
                var localJson = await System.IO.File.ReadAllTextAsync(localPath);
                var notice = JsonSerializer.Deserialize<NoticeInfo>(localJson, AppJsonContext.Default.NoticeInfo);
                if (notice != null) return notice;
            }

            // 本地没有则请求远程
            System.Diagnostics.Debug.WriteLine("Fetching remote announcement...");
            var response = await _httpClient.GetStringAsync(AnnouncementUrl);
            return JsonSerializer.Deserialize<NoticeInfo>(response, AppJsonContext.Default.NoticeInfo);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to fetch announcement: {ex.Message}");
            return null;
        }
    }
}
