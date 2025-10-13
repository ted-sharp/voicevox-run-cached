using Serilog;
using VoicevoxRunCached.Configuration;

namespace VoicevoxRunCached.Services;

/// <summary>
/// LRU（Least Recently Used）キャッシュを実装したメモリキャッシュサービス
/// スレッドセーフな二重連結リストとハッシュマップを使用した効率的なO(1)実装
/// </summary>
public class MemoryCacheService : IDisposable
{
    private readonly Dictionary<string, LinkedListNode<CacheItem>> _cache;
    private readonly TimeSpan _defaultExpiration;
    private readonly ReaderWriterLockSlim _lock;
    private readonly LinkedList<CacheItem> _lruList;
    private readonly long _maxSizeBytes;
    private long _currentSizeBytes;
    private bool _disposed;
    private long _hitCount;
    private long _missCount;

    public MemoryCacheService(CacheSettings settings)
    {
        _maxSizeBytes = settings.MemoryCacheSizeMB * 1024L * 1024L; // MB to bytes
        _defaultExpiration = TimeSpan.FromDays(settings.ExpirationDays);
        _cache = new Dictionary<string, LinkedListNode<CacheItem>>();
        _lruList = new LinkedList<CacheItem>();
        _lock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
        _currentSizeBytes = 0;
        _hitCount = 0;
        _missCount = 0;

        Log.Information("MemoryCacheService を初期化しました - 最大サイズ: {MaxSizeMB}MB, デフォルト有効期限: {ExpirationDays}日",
            settings.MemoryCacheSizeMB, settings.ExpirationDays);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Clear();
            _lock?.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// キャッシュにアイテムを追加または更新
    /// </summary>
    public void Set<T>(string key, T value, TimeSpan? expiration = null)
    {
        if (_disposed)
            return;
        ArgumentNullException.ThrowIfNull(key);

        var expirationTime = DateTime.UtcNow.Add(expiration ?? _defaultExpiration);
        var item = new CacheItem(key, value, expirationTime);
        var estimatedSize = EstimateSize(value);

        _lock.EnterWriteLock();
        try
        {
            // 既存のアイテムを更新する場合
            if (_cache.TryGetValue(key, out var existingNode))
            {
                // 古いサイズを引いて新しいサイズを追加
                _currentSizeBytes -= EstimateSize(existingNode.Value.Value);
                _currentSizeBytes += estimatedSize;

                // ノードの値を更新して最前面に移動
                existingNode.Value = item;
                _lruList.Remove(existingNode);
                _lruList.AddFirst(existingNode);
            }
            else
            {
                // 新しいアイテムを追加
                _currentSizeBytes += estimatedSize;
                var newNode = _lruList.AddFirst(item);
                _cache[key] = newNode;
            }

            // サイズ制限を適用（LRUに基づいて古いアイテムを削除）
            EnforceSizeLimitLocked();

            Log.Debug("キャッシュにアイテムを追加/更新しました - キー: {Key}, サイズ: {Size} bytes", key, estimatedSize);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// キャッシュからアイテムを取得
    /// </summary>
    public T? Get<T>(string key)
    {
        if (_disposed)
            return default;

        _lock.EnterUpgradeableReadLock();
        try
        {
            if (_cache.TryGetValue(key, out var node))
            {
                // 有効期限チェック
                if (node.Value.ExpirationTime <= DateTime.UtcNow)
                {
                    _lock.EnterWriteLock();
                    try
                    {
                        RemoveNodeLocked(node);
                        Interlocked.Increment(ref _missCount);
                    }
                    finally
                    {
                        _lock.ExitWriteLock();
                    }

                    Log.Debug("キャッシュアイテムの有効期限が切れました - キー: {Key}", key);
                    return default;
                }

                // アクセス順序を更新（最前面に移動）
                _lock.EnterWriteLock();
                try
                {
                    _lruList.Remove(node);
                    _lruList.AddFirst(node);
                    Interlocked.Increment(ref _hitCount);
                }
                finally
                {
                    _lock.ExitWriteLock();
                }

                Log.Debug("キャッシュからアイテムを取得しました - キー: {Key}", key);
                return node.Value.Value is T result ? result : default;
            }

            Interlocked.Increment(ref _missCount);
            return default;
        }
        finally
        {
            _lock.ExitUpgradeableReadLock();
        }
    }

    /// <summary>
    /// キャッシュにアイテムが存在するかチェック
    /// </summary>
    public bool Contains(string key)
    {
        if (_disposed)
            return false;

        _lock.EnterReadLock();
        try
        {
            if (_cache.TryGetValue(key, out var node))
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
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// キャッシュからアイテムを削除
    /// </summary>
    public bool Remove(string key)
    {
        if (_disposed)
            return false;

        _lock.EnterWriteLock();
        try
        {
            if (_cache.TryGetValue(key, out var node))
            {
                RemoveNodeLocked(node);
                Log.Debug("キャッシュからアイテムを削除しました - キー: {Key}", key);
                return true;
            }
            return false;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// キャッシュをクリア
    /// </summary>
    public void Clear()
    {
        if (_disposed)
            return;

        _lock.EnterWriteLock();
        try
        {
            _cache.Clear();
            _lruList.Clear();
            _currentSizeBytes = 0;
            Log.Information("キャッシュをクリアしました");
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// キャッシュの統計情報を取得
    /// </summary>
    public CacheStatistics GetStatistics()
    {
        _lock.EnterReadLock();
        try
        {
            var expiredItems = _cache.Values.Count(node => node.Value.ExpirationTime <= DateTime.UtcNow);
            var totalHits = Interlocked.Read(ref _hitCount);
            var totalMisses = Interlocked.Read(ref _missCount);
            var totalRequests = totalHits + totalMisses;

            return new CacheStatistics
            {
                TotalItems = _cache.Count,
                ExpiredItems = expiredItems,
                CurrentSizeBytes = _currentSizeBytes,
                MaxSizeBytes = _maxSizeBytes,
                CacheHits = totalHits,
                CacheMisses = totalMisses,
                HitRate = totalRequests > 0 ? (double)totalHits / totalRequests : 0.0
            };
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// 期限切れアイテムをクリーンアップ
    /// </summary>
    public void CleanupExpiredItems()
    {
        if (_disposed)
            return;

        var expiredNodes = new List<LinkedListNode<CacheItem>>();

        _lock.EnterReadLock();
        try
        {
            foreach (var node in _cache.Values)
            {
                if (node.Value.ExpirationTime <= DateTime.UtcNow)
                {
                    expiredNodes.Add(node);
                }
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }

        if (expiredNodes.Count > 0)
        {
            _lock.EnterWriteLock();
            try
            {
                foreach (var node in expiredNodes)
                {
                    RemoveNodeLocked(node);
                }
            }
            finally
            {
                _lock.ExitWriteLock();
            }

            Log.Information("期限切れアイテムを {Count} 件クリーンアップしました", expiredNodes.Count);
        }
    }

    /// <summary>
    /// サイズ制限を適用（書き込みロック内で呼び出される）
    /// </summary>
    private void EnforceSizeLimitLocked()
    {
        if (_currentSizeBytes <= _maxSizeBytes)
            return;

        Log.Debug("キャッシュサイズ制限に達しました - 現在: {CurrentSize} bytes, 制限: {MaxSize} bytes",
            _currentSizeBytes, _maxSizeBytes);

        // LRU順序で古いアイテムを削除（リストの末尾から）
        var removedCount = 0;
        while (_currentSizeBytes > _maxSizeBytes && _lruList.Count > 0)
        {
            var lastNode = _lruList.Last;
            if (lastNode != null)
            {
                RemoveNodeLocked(lastNode);
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
        _cache.Remove(node.Value.Key);
        _lruList.Remove(node);
        _currentSizeBytes -= EstimateSize(node.Value.Value);
    }

    /// <summary>
    /// オブジェクトのサイズを推定
    /// </summary>
    private long EstimateSize(object? value)
    {
        if (value == null)
            return 0;

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
}

/// <summary>
/// キャッシュアイテム
/// </summary>
public class CacheItem
{
    public CacheItem(string key, object? value, DateTime expirationTime)
    {
        Key = key;
        Value = value;
        ExpirationTime = expirationTime;
    }

    public string Key { get; }
    public object? Value { get; set; }
    public DateTime ExpirationTime { get; }
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

    public double UsagePercentage => MaxSizeBytes > 0 ? (double)CurrentSizeBytes / MaxSizeBytes * 100 : 0;
    public int ValidItems => TotalItems - ExpiredItems;
}
