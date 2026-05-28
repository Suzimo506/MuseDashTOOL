using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Platform;
using MdModManager.Models;

namespace MdModManager.Services;

public class I18nService : INotifyPropertyChanged
{
    private static readonly I18nService _instance = new I18nService();
    public static I18nService Instance => _instance;

    private Dictionary<string, string> _strings = new Dictionary<string, string>();

    public string CurrentLanguage { get; private set; } = System.Globalization.CultureInfo.CurrentUICulture.Name == "zh-CN" ? "zh-CN" : "en-US";

    public string this[string key]
    {
        get
        {
            if (_strings.TryGetValue(key, out var val))
                return val;
            return key; // Fallback to key if not found
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void LoadLanguage(string language)
    {
        CurrentLanguage = language;
        _strings.Clear();

        try
        {
            var uri = new Uri($"avares://MuseDashTOOL/Assets/Locales/{language}.json");
            using var stream = AssetLoader.Open(uri);
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (dict != null)
            {
                _strings = dict;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load language {language}: {ex.Message}");
            // Optional fallback logic here
        }

        OnPropertyChanged("Item"); // Notify indexer changed
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
