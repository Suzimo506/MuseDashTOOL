using System;

namespace MdModManager.Helpers;

public static class SearchHelper
{
    // 检查文本是否匹配查询词（支持模糊匹配和普通包含匹配）
    public static bool IsMatch(string? text, string? query, bool enableFuzzy)
    {
        if (string.IsNullOrEmpty(query)) return true;
        if (string.IsNullOrEmpty(text)) return false;

        if (enableFuzzy)
        {
            var normalizedText = text.Trim().ToLowerInvariant();
            var normalizedQuery = query.Trim().ToLowerInvariant();
            int textIdx = 0;
            int queryIdx = 0;

            while (textIdx < normalizedText.Length && queryIdx < normalizedQuery.Length)
            {
                if (normalizedText[textIdx] == normalizedQuery[queryIdx])
                {
                    queryIdx++;
                }
                textIdx++;
            }

            return queryIdx == normalizedQuery.Length;
        }
        else
        {
            return text.Contains(query, StringComparison.OrdinalIgnoreCase);
        }
    }
}
