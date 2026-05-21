using System;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MdModManager.Models;

public sealed record EuterpeUserInfo(
    [property: JsonPropertyName("uid")] long Uid,
    [property: JsonPropertyName("role")] int Role,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("nickname")] string Nickname,
    [property: JsonPropertyName("avatar_url")] string? AvatarUrl,
    [property: JsonPropertyName("banned")] bool Banned,
    [property: JsonPropertyName("has_github")] bool HasGitHub,
    [property: JsonPropertyName("has_google")] bool HasGoogle);

public sealed partial class AuthState : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvatarUrl))]
    private EuterpeUserInfo? _currentUser;

    public string AvatarUrl => $"https://euterpe-org.com/{CurrentUser?.AvatarUrl}";

    public string? AccessToken { get; set; }
    
    public string? RefreshToken { get; set; }
    
    public DateTimeOffset AccessTokenExpiry { get; set; }

    // 重置全部会话凭证与状态数据
    public void Clear()
    {
        AccessToken = null;
        RefreshToken = null;
        AccessTokenExpiry = default;
        CurrentUser = null;
    }
}
