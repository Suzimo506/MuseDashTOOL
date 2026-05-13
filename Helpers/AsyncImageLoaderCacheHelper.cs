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
    }
}
