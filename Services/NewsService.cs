using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using MdModManager.Helpers;
using MdModManager.Models;

namespace MdModManager.Services;

public interface INewsService
{
    Task<NewsInfo?> GetLatestNewsAsync();
}

// 从远端或本地加载新闻
public class NewsService : INewsService
{
    private const string NewsUrl = "https://raw.githubusercontent.com/Suzimo506/MuseDashTOOL/main/news.json";
    private readonly HttpClient _httpClient;

    public NewsService()
    {
        _httpClient = HttpHelper.CreateOptimizedClient(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(4));
    }

    public async Task<NewsInfo?> GetLatestNewsAsync()
    {
        try
        {
            // 优先读取本地 news.json
            string? localPath = null;
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] possiblePaths = {
                System.IO.Path.Combine(baseDir, "news.json"),
                "news.json",
                System.IO.Path.Combine(Environment.CurrentDirectory, "news.json")
            };

            foreach (var path in possiblePaths)
            {
                if (System.IO.File.Exists(path))
                {
                    localPath = path;
                    break;
                }
            }

            if (localPath == null)
            {
                // 向上追溯最多5级目录，以在开发/调试环境下完美定位项目根目录下的 news.json
                var dir = new System.IO.DirectoryInfo(baseDir);
                for (int i = 0; i < 5 && dir != null; i++)
                {
                    var file = System.IO.Path.Combine(dir.FullName, "news.json");
                    if (System.IO.File.Exists(file))
                    {
                        localPath = file;
                        break;
                    }
                    dir = dir.Parent;
                }
            }

            string jsonContent;
            if (localPath != null)
            {
                var fullPath = System.IO.Path.GetFullPath(localPath);
                RuntimeLog.Write("NewsService", $"检测到本地新闻文件路径: {fullPath}");
                // 显式指定 UTF-8 编码读取，防止在中文 Windows 系统下被误按 GBK 解码导致中文乱码和 BOM 错误
                jsonContent = await System.IO.File.ReadAllTextAsync(localPath, System.Text.Encoding.UTF8);
            }
            else
            {
                RuntimeLog.Write("NewsService", $"没有找到本地新闻文件，开始从远端拉取...");
                jsonContent = await _httpClient.GetStringAsync(NewsUrl);
            }

            if (!string.IsNullOrWhiteSpace(jsonContent))
            {
                jsonContent = jsonContent.Trim();
                // 安全过滤可能残留的 BOM 字符
                if (jsonContent.StartsWith("\uFEFF", StringComparison.Ordinal))
                {
                    jsonContent = jsonContent.Substring(1).Trim();
                    RuntimeLog.Write("NewsService", "已安全过滤残留的 UTF-8 BOM 前缀");
                }

                RuntimeLog.Write("NewsService", $"读取的新闻 JSON 原始内容为: {jsonContent}");

                try
                {
                    var news = JsonSerializer.Deserialize<NewsInfo>(jsonContent, AppJsonContext.Default.NewsInfo);
                    RuntimeLog.Write("NewsService", $"使用 AOT 上下文解析新闻成功: title={news?.Title}");
                    return news;
                }
                catch (Exception ex)
                {
                    RuntimeLog.Write("NewsService", $"使用 AOT 上下文解析发生异常 ({ex.Message})，准备切换回反射解析器...");
                    #pragma warning disable IL2026, IL3050
                    var news = JsonSerializer.Deserialize<NewsInfo>(jsonContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    RuntimeLog.Write("NewsService", $"使用反射解析器解析新闻成功: title={news?.Title}");
                    return news;
                    #pragma warning restore IL2026, IL3050
                }
            }

            RuntimeLog.Write("NewsService", "读取到的新闻 JSON 内容为空。");
            return null;
        }
        catch (Exception ex)
        {
            RuntimeLog.Write("NewsService", $"加载/解析新闻发生异常: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"Failed to fetch news: {ex.Message}");
            return null;
        }
    }
}
