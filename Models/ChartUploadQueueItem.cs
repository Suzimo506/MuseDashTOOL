using CommunityToolkit.Mvvm.ComponentModel;

namespace MdModManager.Models;

public partial class ChartUploadQueueItem : ObservableObject
{
    public required PreparedChartUpload Package { get; init; }

    public string FilePath => Package.SourceFilePath;
    public string FileName => Package.SourceFileName;
    public string Title => Package.OriginalId;
    public string Artist => string.IsNullOrWhiteSpace(Package.Artist) ? "未知曲师" : Package.Artist;
    public string Charter => string.IsNullOrWhiteSpace(Package.Charter) ? "未知谱师" : Package.Charter;
    public string DifficultyText => Package.DifficultyText;

    [ObservableProperty]
    private string _statusText = "等待上传";

    [ObservableProperty]
    private bool _isUploaded;
}
