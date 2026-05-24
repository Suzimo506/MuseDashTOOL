using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using MdModManager.Helpers;
using MdModManager.Models;

namespace MdModManager.Services;

public interface ISponsorService
{
    Task<List<SponsorInfo>?> GetSponsorsAsync();
}

// 加载赞助者名单
public class SponsorService : ISponsorService
{
    public async Task<List<SponsorInfo>?> GetSponsorsAsync()
    {
        try
        {
            // 优先读取本地 sponsored.json
            string? localPath = null;
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] possiblePaths = {
                Path.Combine(baseDir, "sponsored.json"),
                "sponsored.json",
                Path.Combine(Environment.CurrentDirectory, "sponsored.json")
            };

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    localPath = path;
                    break;
                }
            }

            if (localPath == null)
            {
                // 向上追溯最多5级目录，以在开发/调试环境下绝对定位项目根目录下的 sponsored.json
                var dir = new System.IO.DirectoryInfo(baseDir);
                for (int i = 0; i < 5 && dir != null; i++)
                {
                    var file = Path.Combine(dir.FullName, "sponsored.json");
                    if (File.Exists(file))
                    {
                        localPath = file;
                        break;
                    }
                    dir = dir.Parent;
                }
            }

            if (localPath != null)
            {
                var fullPath = Path.GetFullPath(localPath);
                RuntimeLog.Write("SponsorService", $"检测到本地赞助者文件路径: {fullPath}");
                // 显式指定 UTF-8 编码读取，防止中文乱码和 BOM 错误
                var jsonContent = await File.ReadAllTextAsync(localPath, System.Text.Encoding.UTF8);
                if (!string.IsNullOrWhiteSpace(jsonContent))
                {
                    jsonContent = jsonContent.Trim();
                    // 安全过滤可能残留的 BOM 字符
                    if (jsonContent.StartsWith("\uFEFF", StringComparison.Ordinal))
                    {
                        jsonContent = jsonContent.Substring(1).Trim();
                        RuntimeLog.Write("SponsorService", "已安全过滤残留的 UTF-8 BOM 前缀");
                    }

                    try
                    {
                        var list = JsonSerializer.Deserialize<List<SponsorInfo>>(jsonContent, AppJsonContext.Default.ListSponsorInfo);
                        RuntimeLog.Write("SponsorService", "使用 AOT 上下文解析赞助者成功");
                        return list;
                    }
                    catch (Exception ex)
                    {
                        RuntimeLog.Write("SponsorService", $"使用 AOT 上下文解析发生异常 ({ex.Message})，准备切换回反射解析器");
                        #pragma warning disable IL2026, IL3050
                        var list = JsonSerializer.Deserialize<List<SponsorInfo>>(jsonContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        RuntimeLog.Write("SponsorService", "使用反射解析器解析赞助者成功");
                        return list;
                        #pragma warning restore IL2026, IL3050
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load sponsors: {ex.Message}");
        }

        return new List<SponsorInfo>();
    }
}
