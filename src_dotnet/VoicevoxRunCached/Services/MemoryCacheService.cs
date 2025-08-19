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
        this._settings = settings ?? throw new ArgumentNullException(nameof(settings));

        var memoryCacheOptions = new MemoryCacheOptions
        {
            ExpirationScanFrequency = TimeSpan.FromMinutes(5)
        };

        this._memoryCache = new MemoryCache(memoryCacheOptions);

        this._defaultOptions = new MemoryCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromDays(this._settings.ExpirationDays),
            AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(this._settings.ExpirationDays)
        };

        Log.Information("MemoryCacheService を初期化しました - 有効期限: {ExpirationDays}日", this._settings.ExpirationDays);
    }

    public T? Get<T>(string key)
    {
        if (this._memoryCache.TryGetValue(key, out T? value))
        {
            Log.Debug("メモリキャッシュヒット: {Key}", key);
            return value;
        }

        Log.Debug("メモリキャッシュミス: {Key}", key);
        return default;
    }

    public async Task<T?> GetAsync<T>(string key)
    {
        return await Task.FromResult(this.Get<T>(key));
    }

    public void Set<T>(string key, T value, TimeSpan? expiration = null)
    {
        var options = new MemoryCacheEntryOptions();

        // デフォルト設定を適用
        options.SlidingExpiration = this._defaultOptions.SlidingExpiration;
        options.AbsoluteExpirationRelativeToNow = this._defaultOptions.AbsoluteExpirationRelativeToNow;

        if (expiration.HasValue)
        {
            options.AbsoluteExpirationRelativeToNow = expiration;
            options.SlidingExpiration = expiration;
        }

        this._memoryCache.Set(key, value, options);
        Log.Debug("メモリキャッシュに保存: {Key}, 有効期限: {Expiration}", key, expiration ?? TimeSpan.FromDays(this._settings.ExpirationDays));
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? expiration = null)
    {
        this.Set(key, value, expiration);
        await Task.CompletedTask;
    }

    public void Remove(string key)
    {
        this._memoryCache.Remove(key);
        Log.Debug("メモリキャッシュから削除: {Key}", key);
    }

    public async Task RemoveAsync(string key)
    {
        this.Remove(key);
        await Task.CompletedTask;
    }

    public bool TryGetValue<T>(string key, out T? value)
    {
        return this._memoryCache.TryGetValue(key, out value);
    }

    public void Clear()
    {
        if (this._memoryCache is MemoryCache memoryCache)
        {
            memoryCache.Compact(1.0); // 100%のメモリを解放
        }
        Log.Information("メモリキャッシュをクリアしました");
    }

    public async Task ClearAsync()
    {
        this.Clear();
        await Task.CompletedTask;
    }

    public long GetCacheSize()
    {
        // .NET 9.0ではSizeプロパティが利用できないため、簡易的な実装
        return 0;
    }

    public void Dispose()
    {
        this._memoryCache?.Dispose();
        Log.Debug("MemoryCacheService を破棄しました");
    }
}
