using Microsoft.Extensions.Caching.Memory;

namespace VoicevoxRunCached.Services.Cache;

/// <summary>
/// IMemoryCache のラッパ。Hit/Miss/エントリ数の統計と SetSize/Expiration を一元管理する。
/// </summary>
public class MemoryCacheStore
{
    private readonly IMemoryCache _memoryCache;
    private readonly TimeSpan _expiration;

    private long _hitCount;
    private long _missCount;
    private int _entryCount;

    public MemoryCacheStore(IMemoryCache memoryCache, TimeSpan expiration)
    {
        _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
        _expiration = expiration;
    }

    public long HitCount => Interlocked.Read(ref _hitCount);
    public long MissCount => Interlocked.Read(ref _missCount);
    public int EntryCount => _entryCount;

    /// <summary>
    /// メモリキャッシュから取得。ヒット/ミスのカウントも更新する。
    /// </summary>
    public bool TryGet(string key, out byte[]? data)
    {
        if (_memoryCache.TryGetValue(key, out byte[]? cached) && cached != null)
        {
            Interlocked.Increment(ref _hitCount);
            data = cached;
            return true;
        }

        Interlocked.Increment(ref _missCount);
        data = null;
        return false;
    }

    /// <summary>
    /// 統計上のヒットとして記録 (ディスクからの読み戻しなど)
    /// </summary>
    public void RecordHit() => Interlocked.Increment(ref _hitCount);

    /// <summary>
    /// メモリキャッシュにエントリを保存。Compact 時に自動デクリメント。
    /// </summary>
    public void Set(string key, byte[] data)
    {
        var entryOptions = new MemoryCacheEntryOptions()
            .SetSize(data.Length)
            .SetAbsoluteExpiration(_expiration)
            .RegisterPostEvictionCallback((_, _, _, _) => Interlocked.Decrement(ref _entryCount));

        _memoryCache.Set(key, data, entryOptions);
        Interlocked.Increment(ref _entryCount);
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
        Interlocked.Exchange(ref _entryCount, 0);
    }
}
