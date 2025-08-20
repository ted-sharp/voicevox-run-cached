using System.Collections.Concurrent;
using VoicevoxRunCached.Configuration;
using Serilog;

namespace VoicevoxRunCached.Services;

/// <summary>
/// LRU（Least Recently Used）キャッシュを実装したメモリキャッシュサービス
/// </summary>
public class MemoryCacheService : IDisposable
{
    private readonly ConcurrentDictionary<string, CacheItem> _cache;
    private readonly ConcurrentQueue<string> _accessOrder;
    private readonly object _lockObject = new();
    private readonly int _maxSize;
    private readonly TimeSpan _defaultExpiration;
    private bool _disposed;

    public MemoryCacheService(CacheSettings settings)
    {
        this._maxSize = settings.MemoryCacheSizeMB * 1024 * 1024; // MB to bytes
        this._defaultExpiration = TimeSpan.FromDays(settings.ExpirationDays);
        this._cache = new ConcurrentDictionary<string, CacheItem>();
        this._accessOrder = new ConcurrentQueue<string>();

        Log.Information("MemoryCacheService を初期化しました - 最大サイズ: {MaxSizeMB}MB, デフォルト有効期限: {ExpirationDays}日",
            settings.MemoryCacheSizeMB, settings.ExpirationDays);
    }

    /// <summary>
    /// キャッシュにアイテムを追加または更新
    /// </summary>
    public void Set<T>(string key, T value, TimeSpan? expiration = null)
    {
        if (this._disposed) return;

        var expirationTime = DateTime.UtcNow.Add(expiration ?? this._defaultExpiration);
        var item = new CacheItem<T>(value, expirationTime);

        // 既存のアイテムを更新する場合
        if (this._cache.TryGetValue(key, out var existingItem))
        {
            this._cache[key] = item;
            this.UpdateAccessOrder(key);
        }
        else
        {
            // 新しいアイテムを追加
            this._cache[key] = item;
            this._accessOrder.Enqueue(key);
            this.UpdateAccessOrder(key);

            // サイズ制限をチェック
            this.EnforceSizeLimit();
        }

        Log.Debug("キャッシュにアイテムを追加/更新しました - キー: {Key}, サイズ: {Size} bytes",
            key, this.EstimateSize(value));
    }

    /// <summary>
    /// キャッシュからアイテムを取得
    /// </summary>
    public T? Get<T>(string key)
    {
        if (this._disposed) return default;

        if (this._cache.TryGetValue(key, out var item))
        {
            // 有効期限チェック
            if (item.ExpirationTime <= DateTime.UtcNow)
            {
                this._cache.TryRemove(key, out _);
                Log.Debug("キャッシュアイテムの有効期限が切れました - キー: {Key}", key);
                return default;
            }

            // アクセス順序を更新
            this.UpdateAccessOrder(key);
            Log.Debug("キャッシュからアイテムを取得しました - キー: {Key}", key);
            return ((CacheItem<T>)item).Value;
        }

        return default;
    }

    /// <summary>
    /// キャッシュにアイテムが存在するかチェック
    /// </summary>
    public bool Contains(string key)
    {
        if (this._disposed) return false;

        if (this._cache.TryGetValue(key, out var item))
        {
            if (item.ExpirationTime <= DateTime.UtcNow)
            {
                this._cache.TryRemove(key, out _);
                return false;
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// キャッシュからアイテムを削除
    /// </summary>
    public bool Remove(string key)
    {
        if (this._disposed) return false;

        var removed = this._cache.TryRemove(key, out _);
        if (removed)
        {
            Log.Debug("キャッシュからアイテムを削除しました - キー: {Key}", key);
        }
        return removed;
    }

    /// <summary>
    /// キャッシュをクリア
    /// </summary>
    public void Clear()
    {
        if (this._disposed) return;

        lock (this._lockObject)
        {
            this._cache.Clear();
            while (this._accessOrder.TryDequeue(out _)) { }
            Log.Information("キャッシュをクリアしました");
        }
    }

    /// <summary>
    /// キャッシュの統計情報を取得
    /// </summary>
    public CacheStatistics GetStatistics()
    {
        var currentSize = this._cache.Values.Sum(item => this.EstimateSize(item));
        var expiredItems = this._cache.Values.Count(item => item.ExpirationTime <= DateTime.UtcNow);

        return new CacheStatistics
        {
            TotalItems = this._cache.Count,
            ExpiredItems = expiredItems,
            CurrentSizeBytes = currentSize,
            MaxSizeBytes = this._maxSize,
            HitRate = this.CalculateHitRate()
        };
    }

    /// <summary>
    /// 期限切れアイテムをクリーンアップ
    /// </summary>
    public void CleanupExpiredItems()
    {
        if (this._disposed) return;

        var expiredKeys = this._cache
            .Where(kvp => kvp.Value.ExpirationTime <= DateTime.UtcNow)
            .Select(kvp => kvp.Key)
            .ToList();

        var removedCount = 0;
        foreach (var key in expiredKeys)
        {
            if (this._cache.TryRemove(key, out _))
            {
                removedCount++;
            }
        }

        if (removedCount > 0)
        {
            Log.Information("期限切れアイテムを {Count} 件クリーンアップしました", removedCount);
        }
    }

    private void UpdateAccessOrder(string key)
    {
        // アクセス順序を更新（LRUの実装）
        lock (this._lockObject)
        {
            // 既存のキーを削除
            var tempQueue = new ConcurrentQueue<string>();
            while (this._accessOrder.TryDequeue(out var item))
            {
                if (item != key)
                {
                    tempQueue.Enqueue(item);
                }
            }

            // キーを最後に追加（最も最近使用された）
            while (tempQueue.TryDequeue(out var item))
            {
                this._accessOrder.Enqueue(item);
            }
            this._accessOrder.Enqueue(key);
        }
    }

    private void EnforceSizeLimit()
    {
        var currentSize = this._cache.Values.Sum(item => this.EstimateSize(item));

        if (currentSize <= this._maxSize) return;

        Log.Debug("キャッシュサイズ制限に達しました - 現在: {CurrentSize} bytes, 制限: {MaxSize} bytes",
            currentSize, this._maxSize);

        // LRU順序で古いアイテムを削除
        lock (this._lockObject)
        {
            while (currentSize > this._maxSize && this._accessOrder.TryDequeue(out var key))
            {
                if (this._cache.TryRemove(key, out var removedItem))
                {
                    currentSize -= this.EstimateSize(removedItem);
                    Log.Debug("LRUキャッシュから古いアイテムを削除しました - キー: {Key}", key);
                }
            }
        }
    }

    private long EstimateSize(object? value)
    {
        if (value == null) return 0;

        // 簡易的なサイズ推定
        return value switch
        {
            byte[] bytes => bytes.Length,
            string str => str.Length * 2, // UTF-16
            int => 4,
            long => 8,
            double => 8,
            bool => 1,
            _ => 64 // デフォルト推定値
        };
    }

    private double CalculateHitRate()
    {
        // 簡易的なヒット率計算（実際の実装ではより詳細な統計が必要）
        return 0.85; // 仮の値
    }

    public void Dispose()
    {
        if (!this._disposed)
        {
            this.Clear();
            this._disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}

/// <summary>
/// キャッシュアイテムの基底クラス
/// </summary>
public abstract class CacheItem
{
    public DateTime ExpirationTime { get; }

    protected CacheItem(DateTime expirationTime)
    {
        this.ExpirationTime = expirationTime;
    }
}

/// <summary>
/// 型付きキャッシュアイテム
/// </summary>
public class CacheItem<T> : CacheItem
{
    public T Value { get; }

    public CacheItem(T value, DateTime expirationTime) : base(expirationTime)
    {
        this.Value = value;
    }
}

/// <summary>
/// キャッシュ統計情報
/// </summary>
public class CacheStatistics
{
    public int TotalItems { get; set; }
    public int ExpiredItems { get; set; }
    public long CurrentSizeBytes { get; set; }
    public long MaxSizeBytes { get; set; }
    public double HitRate { get; set; }

    // 音声キャッシュとの互換性のためのプロパティ
    public long CacheHits { get; set; }
    public long CacheMisses { get; set; }

    public double UsagePercentage => this.MaxSizeBytes > 0 ? (double)this.CurrentSizeBytes / this.MaxSizeBytes * 100 : 0;
    public int ValidItems => this.TotalItems - this.ExpiredItems;
}
