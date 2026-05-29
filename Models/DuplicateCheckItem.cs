using System;
using System.IO;
using MdModManager.Models;

namespace MdModManager.Models
{
    // 查重时重复谱面项包装实体
    public class DuplicateCheckItem
    {
        // 对应的本地谱面基本信息
        public ChartInfo Chart { get; }

        // 是否被自动或手动勾选为冗余（待删除）谱面
        public bool IsRedundant { get; set; }

        // 该重复组的唯一哈希/特征键
        public string GroupKey { get; }

        public string FilePath => Chart.FilePath;
        public string Name => Chart.Name;
        public string Artist => Chart.MusicAuthor ?? string.Empty;
        public string Charter => Chart.ChartAuthor ?? string.Empty;
        
        public long FileSize { get; }
        public string FileSizeString => $"{FileSize / 1024.0 / 1024.0:F2} MB";
        
        public DateTime LastWriteTime { get; }
        public string LastWriteTimeString => LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");

        public DuplicateCheckItem(ChartInfo chart, string groupKey)
        {
            Chart = chart;
            GroupKey = groupKey;
            
            if (File.Exists(chart.FilePath))
            {
                try
                {
                    var fi = new FileInfo(chart.FilePath);
                    FileSize = fi.Length;
                    LastWriteTime = fi.LastWriteTime;
                }
                catch
                {
                    FileSize = 0;
                    LastWriteTime = DateTime.MinValue;
                }
            }
        }
    }

    // 批量下载时的重复项比对包装实体
    public class BatchDuplicateItem
    {
        // 准备下载的社区谱面
        public MdmcChart Chart { get; }

        // 本地与之冲突的已存在谱面列表
        public System.Collections.Generic.List<ChartInfo> Duplicates { get; }

        // 当前行所选的操作策略：skip (跳过)，overwrite (覆盖)，both (保留两者)
        public string SelectedAction { get; set; } = "skip";

        public BatchDuplicateItem(MdmcChart chart, System.Collections.Generic.List<ChartInfo> duplicates)
        {
            Chart = chart;
            Duplicates = duplicates;
        }
    }
}
