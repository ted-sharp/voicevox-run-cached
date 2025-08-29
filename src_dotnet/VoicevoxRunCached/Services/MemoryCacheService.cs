using VoicevoxRunCached.Configuration;
using Serilog;

namespace VoicevoxRunCached.Services;

/// <summary>
/// LRU（Least Recently Used）キャッシュを実装したメモリキャッシュサービス
/// スレッドセーフな二重連結リストとハッシュマップを使用した効率的なO(1)実装
/// </summary>
public class MemoryCacheService : IDisposable
{
    private readonly Dictionary<string, LinkedListNode<CacheItem>> _cache;
    private readonly LinkedList<CacheItem> _lruList;
    private readonly ReaderWriterLockSlim _lock;
    private readonly long _maxSizeBytes;
    private readonly TimeSpan _defaultExpiration;
    private long _currentSizeBytes;
    private long _hitCount;
    private long _missCount;
    private bool _disposed;

    public MemoryCacheService(CacheSettings settings)
    {
        this._maxSizeBytes = settings.MemoryCacheSizeMB * 1024L * 1024L; // MB to bytes
        this._defaultExpiration = TimeSpan.FromDays(settings.ExpirationDays);
        this._cache = new Dictionary<string, LinkedListNode<CacheItem>>();
        this._lruList = new LinkedList<CacheItem>();
        this._lock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
        this._currentSizeBytes = 0;
        this._hitCount = 0;
        this._missCount = 0;

        Log.Information("MemoryCacheService を初期化しました - 最大サイズ: {MaxSizeMB}MB, デフォルト有効期限: {ExpirationDays}日",
            settings.MemoryCacheSizeMB, settings.ExpirationDays);
    }

    /// <summary>
    /// キャッシュにアイテムを追加または更新
    /// </summary>
    public void Set<T>(string key, T value, TimeSpan? expiration = null)
    {
        if (this._disposed) return;
        ArgumentNullException.ThrowIfNull(key);

        var expirationTime = DateTime.UtcNow.Add(expiration ?? this._defaultExpiration);
        var item = new CacheItem(key, value, expirationTime);
        var estimatedSize = this.EstimateSize(value);

        this._lock.EnterWriteLock();
        try
        {
            // 既存のアイテムを更新する場合
            if (this._cache.TryGetValue(key, out var existingNode))
            {
                // 古いサイズを引いて新しいサイズを追加
                this._currentSizeBytes -= this.EstimateSize(existingNode.Value.Value);
                this._currentSizeBytes += estimatedSize;

                // ノードの値を更新して最前面に移動
                existingNode.Value = item;
                this._lruList.Remove(existingNode);
                this._lruList.AddFirst(existingNode);
            }
            else
            {
                // 新しいアイテムを追加
                this._currentSizeBytes += estimatedSize;
                var newNode = this._lruList.AddFirst(item);
                this._cache[key] = newNode;
            }

            // サイズ制限を適用（LRUに基づいて古いアイテムを削除）
            this.EnforceSizeLimitLocked();

            Log.Debug("キャッシュにアイテムを追加/更新しました - キー: {Key}, サイズ: {Size} bytes", key, estimatedSize);
        }
        finally
        {
            this._lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// キャッシュからアイテムを取得
    /// </summary>
    public T? Get<T>(string key)
    {
        if (this._disposed) return default;

        this._lock.EnterUpgradeableReadLock();
        try
        {
            if (this._cache.TryGetValue(key, out var node))
            {
                // 有効期限チェック
                if (node.Value.ExpirationTime <= DateTime.UtcNow)
                {
                    this._lock.EnterWriteLock();
                    try
                    {
                        this.RemoveNodeLocked(node);
                        Interlocked.Increment(ref this._missCount);
                    }
                    finally
                    {
                        this._lock.ExitWriteLock();
                    }

                    Log.Debug("キャッシュアイテムの有効期限が切れました - キー: {Key}", key);
                    return default;
                }

                // アクセス順序を更新（最前面に移動）
                this._lock.EnterWriteLock();
                try
                {
                    this._lruList.Remove(node);
                    this._lruList.AddFirst(node);
                    Interlocked.Increment(ref this._hitCount);
                }
                finally
                {
                    this._lock.ExitWriteLock();
                }

                Log.Debug("キャッシュからアイテムを取得しました - キー: {Key}", key);
                return node.Value.Value is T result ? result : default;
            }

            Interlocked.Increment(ref this._missCount);
            return default;
        }
        finally
        {
            this._lock.ExitUpgradeableReadLock();
        }
    }

    /// <summary>
    /// キャッシュにアイテムが存在するかチェック
    /// </summary>
    public bool Contains(string key)
    {
        if (this._disposed) return false;

        this._lock.EnterReadLock();
        try
        {
            if (this._cache.TryGetValue(key, out var node))
            {
                if (node.Value.ExpirationTime <= DateTime.UtcNow)
                {
                    // 有効期限切れのアイテムは存在しないものとして扱う
                    return false;
                }
                return true;
            }
            return false;
        }
        finally
        {
            this._lock.ExitReadLock();
        }
    }

    /// <summary>
    /// キャッシュからアイテムを削除
    /// </summary>
    public bool Remove(string key)
    {
        if (this._disposed) return false;

        this._lock.EnterWriteLock();
        try
        {
            if (this._cache.TryGetValue(key, out var node))
            {
                this.RemoveNodeLocked(node);
                Log.Debug("キャッシュからアイテムを削除しました - キー: {Key}", key);
                return true;
            }
            return false;
        }
        finally
        {
            this._lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// キャッシュをクリア
    /// </summary>
    public void Clear()
    {
        if (this._disposed) return;

        this._lock.EnterWriteLock();
        try
        {
            this._cache.Clear();
            this._lruList.Clear();
            this._currentSizeBytes = 0;
            Log.Information("キャッシュをクリアしました");
        }
        finally
        {
            this._lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// キャッシュの統計情報を取得
    /// </summary>
    public CacheStatistics GetStatistics()
    {
        this._lock.EnterReadLock();
        try
        {
            var expiredItems = this._cache.Values.Count(node => node.Value.ExpirationTime <= DateTime.UtcNow);
            var totalHits = Interlocked.Read(ref this._hitCount);
            var totalMisses = Interlocked.Read(ref this._missCount);
            var totalRequests = totalHits + totalMisses;

            return new CacheStatistics
            {
                TotalItems = this._cache.Count,
                ExpiredItems = expiredItems,
                CurrentSizeBytes = this._currentSizeBytes,
                MaxSizeBytes = this._maxSizeBytes,
                CacheHits = totalHits,
                CacheMisses = totalMisses,
                HitRate = totalRequests > 0 ? (double)totalHits / totalRequests : 0.0
            };
        }
        finally
        {
            this._lock.ExitReadLock();
        }
    }

    /// <summary>
    /// 期限切れアイテムをクリーンアップ
    /// </summary>
    public void CleanupExpiredItems()
    {
        if (this._disposed) return;

        var expiredNodes = new List<LinkedListNode<CacheItem>>();

        this._lock.EnterReadLock();
        try
        {
            foreach (var node in this._cache.Values)
            {
                if (node.Value.ExpirationTime <= DateTime.UtcNow)
                {
                    expiredNodes.Add(node);
                }
            }
        }
        finally
        {
            this._lock.ExitReadLock();
        }

        if (expiredNodes.Count > 0)
        {
            this._lock.EnterWriteLock();
            try
            {
                foreach (var node in expiredNodes)
                {
                    this.RemoveNodeLocked(node);
                }
            }
            finally
            {
                this._lock.ExitWriteLock();
            }

            Log.Information("期限切れアイテムを {Count} 件クリーンアップしました", expiredNodes.Count);
        }
    }

    /// <summary>
    /// サイズ制限を適用（書き込みロック内で呼び出される）
    /// </summary>
    private void EnforceSizeLimitLocked()
    {
        if (this._currentSizeBytes <= this._maxSizeBytes) return;

        Log.Debug("キャッシュサイズ制限に達しました - 現在: {CurrentSize} bytes, 制限: {MaxSize} bytes",
            this._currentSizeBytes, this._maxSizeBytes);

        // LRU順序で古いアイテムを削除（リストの末尾から）
        var removedCount = 0;
        while (this._currentSizeBytes > this._maxSizeBytes && this._lruList.Count > 0)
        {
            var lastNode = this._lruList.Last;
            if (lastNode != null)
            {
                this.RemoveNodeLocked(lastNode);
                removedCount++;
                Log.Debug("LRUキャッシュから古いアイテムを削除しました - キー: {Key}", lastNode.Value.Key);
            }
        }

        if (removedCount > 0)
        {
            Log.Information("LRU制限により {Count} 件のアイテムを削除しました", removedCount);
        }
    }

    /// <summary>
    /// ノードを削除（書き込みロック内で呼び出される）
    /// </summary>
    private void RemoveNodeLocked(LinkedListNode<CacheItem> node)
    {
        this._cache.Remove(node.Value.Key);
        this._lruList.Remove(node);
        this._currentSizeBytes -= this.EstimateSize(node.Value.Value);
    }

    /// <summary>
    /// オブジェクトのサイズを推定
    /// </summary>
    private long EstimateSize(object? value)
    {
        if (value == null) return 0;

        // より正確なサイズ推定
        return value switch
        {
            byte[] bytes => bytes.Length + 24, // 配列のオーバーヘッド
            string str => (str.Length * 2) + 26, // UTF-16 + 文字列のオーバーヘッド
            int => 4 + 16, // 値 + オブジェクトヘッダ
            long => 8 + 16,
            double => 8 + 16,
            bool => 1 + 16,
            _ => 64 // デフォルト推定値
        };
    }

    public void Dispose()
    {
        if (!this._disposed)
        {
            this.Clear();
            this._lock?.Dispose();
            this._disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}

/// <summary>
/// キャッシュアイテム
/// </summary>
public class CacheItem
{
    public string Key { get; }
    public object? Value { get; set; }
    public DateTime ExpirationTime { get; }

    public CacheItem(string key, object? value, DateTime expirationTime)
    {
        this.Key = key;
        this.Value = value;
        this.ExpirationTime = expirationTime;
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
    public long CacheHits { get; set; }
    public long CacheMisses { get; set; }
    public double HitRate { get; set; }

    public double UsagePercentage => this.MaxSizeBytes > 0 ? (double)this.CurrentSizeBytes / this.MaxSizeBytes * 100 : 0;
    public int ValidItems => this.TotalItems - this.ExpiredItems;
}
