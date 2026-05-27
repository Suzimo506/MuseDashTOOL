using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace MdModManager.Helpers;

public static class BingTranslateHelper
{
    private static readonly HttpClient _httpClient = new HttpClient();

    static BingTranslateHelper()
    {
        // 伪装成浏览器，避免被拦截
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36 Edg/122.0.0.0");
    }
    private static string? _authToken;
    private static DateTime _tokenExpiration = DateTime.MinValue;

    private static async Task<string> GetTokenAsync()
    {
        if (_authToken != null && DateTime.Now < _tokenExpiration)
        {
            return _authToken;
        }

        try
        {
            _authToken = await _httpClient.GetStringAsync("https://edge.microsoft.com/translate/auth");
            _tokenExpiration = DateTime.Now.AddMinutes(9);
            return _authToken;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BingTranslate] Token Get Failed: {ex.Message}");
            return string.Empty;
        }
    }

    public static async Task<string> TranslateToChineseAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        
        var result = await TranslateAsync(new List<string> { text });
        return result.Count > 0 ? result[0] : text;
    }

    public static async Task<List<string>> TranslateAsync(List<string> texts)
    {
        if (texts == null || texts.Count == 0) return new List<string>();

        var token = await GetTokenAsync();
        Console.WriteLine($"[BingTranslate] Got Token: {(string.IsNullOrEmpty(token) ? "NULL" : "OK")}");
        if (string.IsNullOrEmpty(token)) return texts;

        try
        {
            var targetLang = MdModManager.Services.I18nService.Instance.CurrentLanguage == "en-US" ? "en" : "zh-Hans";
            var url = $"https://api-edge.cognitive.microsofttranslator.com/translate?from=&to={targetLang}&api-version=3.0";
            
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            
            var body = texts.Select(t => new { Text = t }).ToArray();
            var jsonContent = JsonSerializer.Serialize(body);
            request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            Console.WriteLine($"[BingTranslate] HTTP Status: {response.StatusCode}");
            if (!response.IsSuccessStatusCode) return texts;

            var responseJson = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"[BingTranslate] Raw JSON Response: {responseJson}");
            using var doc = JsonDocument.Parse(responseJson);
            
            var translatedTexts = new List<string>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.TryGetProperty("translations", out var translations) && 
                    translations.ValueKind == JsonValueKind.Array && 
                    translations.GetArrayLength() > 0)
                {
                    var translation = translations[0];
                    if (translation.TryGetProperty("text", out var translatedText))
                    {
                        translatedTexts.Add(translatedText.GetString() ?? "");
                    }
                    else
                    {
                        translatedTexts.Add("");
                    }
                }
                else
                {
                    translatedTexts.Add("");
                }
            }
            
            // 确保返回的数组长度和原数组一致
            for (int i = 0; i < texts.Count; i++)
            {
                if (i >= translatedTexts.Count || string.IsNullOrEmpty(translatedTexts[i]))
                {
                    if (translatedTexts.Count <= i)
                        translatedTexts.Add(texts[i]);
                    else
                        translatedTexts[i] = texts[i];
                }
            }

            return translatedTexts;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Bing translation failed: {ex.Message}");
            return texts;
        }
    }
}
