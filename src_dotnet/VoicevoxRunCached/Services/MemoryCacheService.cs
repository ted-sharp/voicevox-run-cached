using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using VoicevoxRunCached.Configuration;
using Serilog;

namespace VoicevoxRunCached.Services;

public class MemoryCacheService : IDisposable
{
    private readonly IMemoryCache _memoryCache;
    private readonly CacheSettings _settings;
    private readonly MemoryCacheEntryOptions _defaultOptions;

    public MemoryCacheService(CacheSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        var memoryCacheOptions = new MemoryCacheOptions
        {
            ExpirationScanFrequency = TimeSpan.FromMinutes(5)
        };

        _memoryCache = new MemoryCache(memoryCacheOptions);

        _defaultOptions = new MemoryCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromDays(_settings.ExpirationDays),
            AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(_settings.ExpirationDays)
        };

        Log.Information("MemoryCacheService を初期化しました - 有効期限: {ExpirationDays}日", _settings.ExpirationDays);
    }

    public T? Get<T>(string key)
    {
        if (_memoryCache.TryGetValue(key, out T? value))
        {
            Log.Debug("メモリキャッシュヒット: {Key}", key);
            return value;
        }

        Log.Debug("メモリキャッシュミス: {Key}", key);
        return default;
    }

    public async Task<T?> GetAsync<T>(string key)
    {
        return await Task.FromResult(Get<T>(key));
    }

    public void Set<T>(string key, T value, TimeSpan? expiration = null)
    {
        var options = new MemoryCacheEntryOptions();

        // デフォルト設定を適用
        options.SlidingExpiration = _defaultOptions.SlidingExpiration;
        options.AbsoluteExpirationRelativeToNow = _defaultOptions.AbsoluteExpirationRelativeToNow;

        if (expiration.HasValue)
        {
            options.AbsoluteExpirationRelativeToNow = expiration;
            options.SlidingExpiration = expiration;
        }

        _memoryCache.Set(key, value, options);
        Log.Debug("メモリキャッシュに保存: {Key}, 有効期限: {Expiration}", key, expiration ?? TimeSpan.FromDays(_settings.ExpirationDays));
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? expiration = null)
    {
        Set(key, value, expiration);
        await Task.CompletedTask;
    }

    public void Remove(string key)
    {
        _memoryCache.Remove(key);
        Log.Debug("メモリキャッシュから削除: {Key}", key);
    }

    public async Task RemoveAsync(string key)
    {
        Remove(key);
        await Task.CompletedTask;
    }

    public bool TryGetValue<T>(string key, out T? value)
    {
        return _memoryCache.TryGetValue(key, out value);
    }

    public void Clear()
    {
        if (_memoryCache is MemoryCache memoryCache)
        {
            memoryCache.Compact(1.0); // 100%のメモリを解放
        }
        Log.Information("メモリキャッシュをクリアしました");
    }

    public async Task ClearAsync()
    {
        Clear();
        await Task.CompletedTask;
    }

    public long GetCacheSize()
    {
        // .NET 9.0ではSizeプロパティが利用できないため、簡易的な実装
        return 0;
    }

    public void Dispose()
    {
        _memoryCache?.Dispose();
        Log.Debug("MemoryCacheService を破棄しました");
    }
}
