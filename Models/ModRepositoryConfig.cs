using System.Text.Json.Serialization;

namespace MdModManager.Models;

/// <summary>
/// 远端 mod 仓库配置，用于动态切换 mod 列表获取地址。
/// 托管在 GitHub 仓库根目录的 mod_repository.json。
/// </summary>
public class ModRepositoryConfig
{
    /// <summary>
    /// 覆盖 mod 列表的获取地址。
    /// 为空字符串时不覆盖，使用默认的 Gitee 源。
    /// </summary>
    [JsonPropertyName("mod_links_url")]
    public string ModLinksUrl { get; set; } = "";
}
