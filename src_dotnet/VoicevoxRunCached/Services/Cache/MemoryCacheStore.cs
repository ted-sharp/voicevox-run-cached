using Microsoft.Extensions.Caching.Memory;

namespace VoicevoxRunCached.Services.Cache;

/// <summary>
/// MemoryCache のラッパ。SetSize/Expiration を一元管理する。
/// Clear() で MemoryCache.Compact(1.0) を呼ぶ必要があるので具象 MemoryCache を直接持つ。
/// </summary>
public class MemoryCacheStore
{
    private readonly MemoryCache _memoryCache;
    private readonly TimeSpan _expiration;

    public MemoryCacheStore(MemoryCache memoryCache, TimeSpan expiration)
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
    /// 全エントリを退避。
    /// </summary>
    public void Clear() => _memoryCache.Compact(1.0);
}
