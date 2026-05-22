using AsyncImageLoader;
using AsyncImageLoader.Loaders;

namespace MdModManager.Helpers;

public static class AsyncImageLoaderCacheHelper
{
    public static void ClearMemoryCache()
    {
        if (ImageLoader.AsyncImageLoader is RamCachedWebImageLoader ramCachedLoader)
        {
            ramCachedLoader.ClearRamCache();
        }
        else if (ImageLoader.AsyncImageLoader is DiskCachedWebImageLoader diskCachedLoader)
        {
            diskCachedLoader.ClearRamCache();
        }

        // 清理QQ群专属加速图片加载器的内存缓存
        if (OptimizedImageLoader.QQGroupCoverLoader is RamCachedWebImageLoader qqRamLoader)
        {
            qqRamLoader.ClearRamCache();
        }
        else if (OptimizedImageLoader.QQGroupCoverLoader is DiskCachedWebImageLoader qqDiskLoader)
        {
            qqDiskLoader.ClearRamCache();
        }
    }
}
