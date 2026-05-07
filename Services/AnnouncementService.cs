using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using MdModManager.Helpers;
using MdModManager.Models;

namespace MdModManager.Services;

public interface IAnnouncementService
{
    Task<System.Collections.Generic.List<NoticeInfo>?> GetLatestAnnouncementsAsync();
}

public class AnnouncementService : IAnnouncementService
{
    private const string AnnouncementUrl = "https://raw.githubusercontent.com/Suzimo506/MuseDashTOOL/main/announcement.json";
    private readonly HttpClient _httpClient;

    public AnnouncementService()
    {
        _httpClient = HttpHelper.CreateOptimizedClient(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(4));
        // 设置较短的超时时间，避免网络环境差时卡顿
    }

    public async Task<System.Collections.Generic.List<NoticeInfo>?> GetLatestAnnouncementsAsync()
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

            string jsonContent = "";
            if (localPath != null)
            {
                System.Diagnostics.Debug.WriteLine($"Loading local announcement from: {System.IO.Path.GetFullPath(localPath)}");
                jsonContent = await System.IO.File.ReadAllTextAsync(localPath);
            }
            else
            {
                // 本地没有则请求远程
                System.Diagnostics.Debug.WriteLine("Fetching remote announcement...");
                jsonContent = await _httpClient.GetStringAsync(AnnouncementUrl);
            }

            if (!string.IsNullOrWhiteSpace(jsonContent))
            {
                try
                {
                    // 优先尝试解析为数组
                    var list = JsonSerializer.Deserialize<System.Collections.Generic.List<NoticeInfo>>(jsonContent, AppJsonContext.Default.ListNoticeInfo);
                    if (list != null) return list;
                }
                catch
                {
                    // 如果不是数组，则尝试解析为单个对象（向下兼容）
                    var single = JsonSerializer.Deserialize<NoticeInfo>(jsonContent, AppJsonContext.Default.NoticeInfo);
                    if (single != null) return new System.Collections.Generic.List<NoticeInfo> { single };
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to fetch announcement: {ex.Message}");
            return null;
        }
    }
}
