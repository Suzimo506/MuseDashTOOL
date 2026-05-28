using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;
using System.IO;

namespace MdModManager.Models;

public partial class ChartInfo : ObservableObject
{
    /// <summary>mdm 文件的完整路径</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>歌曲名称（来自 info.json 或文件名）</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>歌曲作者/作曲</summary>
    public string? MusicAuthor { get; set; }

    /// <summary>谱面设计者/Mapper</summary>
    public string? ChartAuthor { get; set; }

    /// <summary>难度列表（如 Easy / Hard / Master）</summary>
    public List<string> Difficulties { get; set; } = new();

    /// <summary>BPM信息</summary>
    public string? Bpm { get; set; }

    /// <summary>从 ZIP 内 PNG 加载的封面图</summary>
    public Bitmap? CoverImage { get; set; }

    /// <summary>封面资源路径（支持本地临时文件与 GIF 动图）</summary>
    public string? CoverSource { get; set; }

    public bool HasCoverSource => !string.IsNullOrWhiteSpace(CoverSource);

    public bool HasAnimatedCoverSource => HasGifLikeSource(CoverSource);

    public bool HasStaticCoverSource => HasCoverSource && !HasAnimatedCoverSource;

    public string? AnimatedCoverSource => HasAnimatedCoverSource ? CoverSource : null;

    public bool HasCoverBitmap => CoverImage != null;

    public bool HasAnyCover => HasAnimatedCoverSource || HasCoverBitmap;

    internal bool HasTemporaryCoverFile { get; set; }

    /// <summary>ZIP 内试听音频的 entry 名称</summary>
    public string? DemoEntryName { get; set; }

    /// <summary>是否正在试听中</summary>
    [ObservableProperty]

    private bool _isPlaying;

    /// <summary>是否在本次进程中新下载的</summary>
    [ObservableProperty]
    private bool _isNewDownload;

    // 批量选中状态
    [ObservableProperty]
    private bool _isSelected;

    // 衍生曲师文本
    public string DisplayArtist
    {
        get
        {
            if (string.IsNullOrWhiteSpace(MusicAuthor)) return string.Empty;
            var idx = MusicAuthor.IndexOf('\n');
            var firstLine = idx >= 0 ? MusicAuthor.Substring(0, idx) : MusicAuthor;
            var clean = firstLine.Trim();
            bool isEn = MdModManager.Services.I18nService.Instance.CurrentLanguage == "en-US";
            return string.IsNullOrEmpty(clean) ? string.Empty : (isEn ? $"Music: {clean}" : $"曲：{clean}");
        }
    }

    // 衍生谱师文本
    public string DisplayCharter
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ChartAuthor)) return string.Empty;
            var idx = ChartAuthor.IndexOf('\n');
            var firstLine = idx >= 0 ? ChartAuthor.Substring(0, idx) : ChartAuthor;
            var clean = firstLine.Trim();
            bool isEn = MdModManager.Services.I18nService.Instance.CurrentLanguage == "en-US";
            return string.IsNullOrEmpty(clean) ? string.Empty : (isEn ? $"Charter: {clean}" : $"谱：{clean}");
        }
    }

    /// <summary>副标题展示（作曲 + 谱师）</summary>
    public string SubInfo
    {
        get
        {
            var parts = new List<string>();
            bool isEn = MdModManager.Services.I18nService.Instance.CurrentLanguage == "en-US";
            if (!string.IsNullOrEmpty(MusicAuthor)) parts.Add(isEn ? $"Music: {MusicAuthor}" : $"曲：{MusicAuthor}");
            if (!string.IsNullOrEmpty(ChartAuthor)) parts.Add(isEn ? $"Charter: {ChartAuthor}" : $"谱：{ChartAuthor}");
            return string.Join(" | ", parts);
        }
    }

    /// <summary>难度标签文字（逗号连接）</summary>
    // 谱面所属分类名称
    public string CategoryName
    {
        get
        {
            if (string.IsNullOrEmpty(FilePath)) return "未分类";
            var parent = Path.GetFileName(Path.GetDirectoryName(FilePath));
            return parent == "Custom_Albums" ? "未分类" : (parent ?? "未分类");
        }
    }

    /// <summary>难度标签文字（逗号连接）</summary>
    public string DifficultyText => Difficulties.Count > 0
        ? string.Join(" / ", Difficulties)
        : string.Empty;

    public void CleanupCoverResources()
    {
        CoverImage?.Dispose();
        CoverImage = null;

        if (HasTemporaryCoverFile && !string.IsNullOrWhiteSpace(CoverSource))
        {
            try
            {
                var uri = new System.Uri(CoverSource, System.UriKind.Absolute);
                if (uri.IsFile && System.IO.File.Exists(uri.LocalPath))
                {
                    System.IO.File.Delete(uri.LocalPath);
                }
            }
            catch
            {
            }
        }

        CoverSource = null;
        HasTemporaryCoverFile = false;
    }

    private static bool HasGifLikeSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return false;

        if (System.Uri.TryCreate(source, System.UriKind.Absolute, out var uri))
        {
            var path = uri.IsFile ? uri.LocalPath : uri.AbsolutePath;
            return string.Equals(Path.GetExtension(path), ".gif", System.StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(Path.GetExtension(source), ".gif", System.StringComparison.OrdinalIgnoreCase);
    }
}
