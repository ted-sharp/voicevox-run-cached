using Microsoft.Extensions.Caching.Memory;

namespace VoicevoxRunCached.Services.Cache;

/// <summary>
/// IMemoryCache のラッパ。SetSize/Expiration を一元管理する。
/// </summary>
public class MemoryCacheStore
{
    private readonly IMemoryCache _memoryCache;
    private readonly TimeSpan _expiration;

    public MemoryCacheStore(IMemoryCache memoryCache, TimeSpan expiration)
    {
        _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
        _expiration = expiration;
    }

    /// <summary>
    /// メモリキャッシュから取得。
    /// </summary>
    public bool TryGet(string key, out byte[]? data)
    {
        if (_memoryCache.TryGetValue(key, out byte[]? cached) && cached != null)
        {
            data = cached;
            return true;
        }

        data = null;
        return false;
    }

    /// <summary>
    /// メモリキャッシュにエントリを保存。
    /// </summary>
    public void Set(string key, byte[] data)
    {
        var entryOptions = new MemoryCacheEntryOptions()
            .SetSize(data.Length)
            .SetAbsoluteExpiration(_expiration);

        _memoryCache.Set(key, data, entryOptions);
    }

    /// <summary>
    /// 全エントリを退避 (MemoryCache 限定)
    /// </summary>
    public void Clear()
    {
        if (_memoryCache is MemoryCache mc)
        {
            mc.Compact(1.0);
        }
    }
}
