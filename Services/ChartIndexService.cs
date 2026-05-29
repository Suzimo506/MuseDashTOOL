using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MdModManager.Models;

namespace MdModManager.Services
{
    // 全局自制谱索引服务接口，提供基于特征比对的高速内存检索
    public interface IChartIndexService
    {
        void IndexAll(IEnumerable<ChartInfo> localCharts);
        void AddToIndex(ChartInfo chart);
        void RemoveFromIndex(string filePath);
        List<ChartInfo> FindDuplicatesOf(string title, string artist, string charter);
        List<ChartInfo> FindDuplicatesByFileName(string fileName);
        void Clear();
    }

    // 谱面索引服务实现类，采用标准化文本与文件名双重哈希检索
    public class ChartIndexService : IChartIndexService
    {
        private readonly ConcurrentDictionary<string, ChartInfo> _filePathMap = new(StringComparer.OrdinalIgnoreCase);

        public void IndexAll(IEnumerable<ChartInfo> localCharts)
        {
            _filePathMap.Clear();
            if (localCharts == null) return;
            foreach (var chart in localCharts)
            {
                if (chart != null && !string.IsNullOrEmpty(chart.FilePath))
                {
                    _filePathMap[chart.FilePath] = chart;
                }
            }
        }

        public void AddToIndex(ChartInfo chart)
        {
            if (chart != null && !string.IsNullOrEmpty(chart.FilePath))
            {
                _filePathMap[chart.FilePath] = chart;
            }
        }

        public void RemoveFromIndex(string filePath)
        {
            if (!string.IsNullOrEmpty(filePath))
            {
                _filePathMap.TryRemove(filePath, out _);
            }
        }

        // 匹配规则：标准化歌曲名称相同，且标准化谱师或曲作者一致时，视作重复谱面
        public List<ChartInfo> FindDuplicatesOf(string title, string artist, string charter)
        {
            var duplicates = new List<ChartInfo>();
            var normTitle = Normalize(title);
            if (string.IsNullOrEmpty(normTitle)) return duplicates;

            var normArtist = Normalize(artist);
            var normCharter = Normalize(charter);

            foreach (var kvp in _filePathMap)
            {
                var local = kvp.Value;
                var localTitle = Normalize(local.Name);
                if (localTitle == normTitle)
                {
                    var localArtist = Normalize(local.MusicAuthor);
                    var localCharter = Normalize(local.ChartAuthor);

                    bool artistMatch = string.IsNullOrEmpty(normArtist) || string.IsNullOrEmpty(localArtist) || normArtist == localArtist;
                    bool charterMatch = string.IsNullOrEmpty(normCharter) || string.IsNullOrEmpty(localCharter) || normCharter == localCharter;

                    if (artistMatch && charterMatch)
                    {
                        duplicates.Add(local);
                    }
                }
            }

            return duplicates;
        }

        public List<ChartInfo> FindDuplicatesByFileName(string fileName)
        {
            var duplicates = new List<ChartInfo>();
            if (string.IsNullOrEmpty(fileName)) return duplicates;

            var targetName = Path.GetFileName(fileName);
            foreach (var kvp in _filePathMap)
            {
                var localName = Path.GetFileName(kvp.Key);
                if (string.Equals(localName, targetName, StringComparison.OrdinalIgnoreCase))
                {
                    duplicates.Add(kvp.Value);
                }
            }
            return duplicates;
        }

        public void Clear()
        {
            _filePathMap.Clear();
        }

        // 去除所有空格、特殊标点与大小写差异，防止轻微命名差异导致拦截失败
        private string Normalize(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            var chars = s.ToLowerInvariant()
                         .Where(c => char.IsLetterOrDigit(c))
                         .ToArray();
            return new string(chars);
        }
    }
}
