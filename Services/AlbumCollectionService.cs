using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using MdModManager.Models;

namespace MdModManager.Services;

public interface IAlbumCollectionService
{
    Task<List<DesignerCategory>> GetCollectionsAsync();
}

public class AlbumCollectionService : IAlbumCollectionService
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    
    private const string IndexUrl = "https://ghproxy.net/https://raw.githubusercontent.com/KuoKing506/CustomAlbums_Collection/main/designers.json";

    public async Task<List<DesignerCategory>> GetCollectionsAsync()
    {
        try
        {
            var collections = await _http.GetFromJsonAsync<List<DesignerCategory>>(IndexUrl);
            return collections ?? new List<DesignerCategory>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AlbumCollectionService] Failed to load JSON: {ex.Message}");
            return new List<DesignerCategory>();
        }
    }
}
