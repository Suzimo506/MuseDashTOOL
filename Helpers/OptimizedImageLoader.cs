using System;
using System.Net.Http;
using AsyncImageLoader;
using AsyncImageLoader.Loaders;

namespace MdModManager.Helpers;

// 专用于QQ群谱面封面的加速图片加载器，使用具有优选IP功能的客户端进行下载
public static class OptimizedImageLoader
{
    public static IAsyncImageLoader QQGroupCoverLoader { get; } =
        new RamCachedWebImageLoader(HttpHelper.CreateOptimizedClient(TimeSpan.FromSeconds(30), forceOptimized: true), true);
}
