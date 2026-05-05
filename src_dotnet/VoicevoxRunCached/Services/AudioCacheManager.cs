using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services.Cache;

namespace VoicevoxRunCached.Services;

/// <summary>
/// 音声キャッシュの統合ファサード。メモリ・ディスクストア、eviction、統計収集を組み合わせて公開 API を提供する。
/// </summary>
public class AudioCacheManager : IDisposable
{
    private readonly IMemoryCache _memoryCache;
    private readonly MemoryCacheStore _memory;
    private readonly DiskCacheStore _disk;
    private readonly CacheEvictionPolicy _eviction;
    private readonly CacheSettings _settings;
    private readonly ILogger _logger;
    // 起動済みだが完了していないバックグラウンド eviction を Dispose 時に待機するための追跡セット。
    // SaveAudioCacheAsync は fire-and-forget だが、プロセス終了時に走行中タスクが mp3/meta を
    // 中途半端な状態で残さないよう、Dispose で短時間待ち合わせる。
    private readonly HashSet<Task> _pendingEvictions = new();
    private readonly object _pendingEvictionsLock = new();
    private bool _disposed;

    public AudioCacheManager(CacheSettings settings, IMemoryCache memoryCache, ILogger? logger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        _memory = new MemoryCacheStore(memoryCache, TimeSpan.FromDays(settings.ExpirationDays));
        _disk = new DiskCacheStore(settings, _logger);
        _eviction = new CacheEvictionPolicy(_disk, settings, _logger);

        _logger.LogInformation("AudioCacheManager を初期化しました - キャッシュディレクトリ: {CacheDir}, メモリキャッシュ: 有効, 最大サイズ: {MaxSizeMB}MB",
            _disk.Directory, settings.MemoryCacheSizeMb);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;
        if (disposing)
        {
            try
            {
                Task[] pending;
                lock (_pendingEvictionsLock)
                {
                    pending = _pendingEvictions.ToArray();
                }
                if (pending.Length > 0)
                {
                    // 走行中の eviction が中途半端に終了しないよう短時間待つ
                    Task.WhenAll(pending).Wait(TimeSpan.FromSeconds(2));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "走行中の eviction 待機中に警告が発生しました");
            }

            try
            {
                if (_memoryCache is MemoryCache mc)
                {
                    mc.Dispose();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AudioCacheManagerの破棄中にエラーが発生しました");
            }
        }
        _disposed = true;
    }

    /// <summary>
    /// キャッシュから音声データを取得する。メモリ→ディスクの順で fallback。
    /// </summary>
    public Task<byte[]?> GetCachedAudioAsync(VoiceRequest request, CancellationToken cancellationToken = default)
    {
        var cacheKey = CacheKeyGenerator.Compute(request);
        return WithCacheErrorHandling(CacheErrorMapper.OperationKind.Read, "キャッシュからの音声データ取得", cacheKey, async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_memory.TryGet(cacheKey, out byte[]? memoryCached) && memoryCached != null)
            {
                _logger.LogDebug("メモリキャッシュヒット: {CacheKey} - サイズ: {Size} bytes", cacheKey, memoryCached.Length);
                return memoryCached;
            }

            var audioData = await _disk.LoadAsync(cacheKey, cancellationToken);
            if (audioData != null)
            {
                _memory.Set(cacheKey, audioData);
                return audioData;
            }

            return null;
        });
    }

    /// <summary>
    /// 音声データをキャッシュに保存する。WAV はディスク保存前に MP3 に変換される。
    /// </summary>
    public Task SaveAudioCacheAsync(VoiceRequest request, byte[] audioData, CancellationToken cancellationToken = default)
    {
        var cacheKey = CacheKeyGenerator.Compute(request);
        return WithCacheErrorHandling<object?>(CacheErrorMapper.OperationKind.Write, "キャッシュの保存", cacheKey, async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var mp3Data = AudioConversionUtility.ConvertWavToMp3(audioData);
            await _disk.SaveAsync(request, mp3Data, cacheKey, cancellationToken);
            _memory.Set(cacheKey, mp3Data);

            _logger.LogDebug("キャッシュを保存しました: {CacheKey} - サイズ: {Size} bytes", cacheKey, mp3Data.Length);

            TrackBackgroundEviction(_eviction.RunBackgroundSizeCleanupAsync());
            return null;
        });
    }

    private void TrackBackgroundEviction(Task task)
    {
        lock (_pendingEvictionsLock)
        {
            _pendingEvictions.Add(task);
        }
        task.ContinueWith(t =>
        {
            lock (_pendingEvictionsLock)
            {
                _pendingEvictions.Remove(t);
            }
        }, TaskScheduler.Default);
    }

    public Task CleanupExpiredCacheAsync(CancellationToken cancellationToken = default)
        => _eviction.CleanupExpiredAsync(cancellationToken);

    public Task CleanupByMaxSizeAsync(CancellationToken cancellationToken = default)
        => _eviction.CleanupByMaxSizeAsync(cancellationToken);

    public Task ClearAllCacheAsync(CancellationToken cancellationToken = default)
    {
        return WithCacheErrorHandling<object?>(CacheErrorMapper.OperationKind.Clear, "キャッシュのクリア", null, () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            _memory.Clear();
            foreach (var file in _disk.GetAudioFiles())
            {
                _disk.Delete(Path.GetFileNameWithoutExtension(file));
            }
            _logger.LogInformation("すべてのキャッシュをクリアしました - メモリ・ディスク共にクリア済み");
            return Task.FromResult<object?>(null);
        });
    }

    /// <summary>
    /// 複数セグメントを順次処理する。各セグメントはキャッシュからの取得を試み、ヒット時に AudioData を埋める。
    /// </summary>
    /// <remarks>
    /// 引数 <paramref name="segments"/> を in-place で mutate し、同じ List を返す。
    /// 並列化は I/O 集中時にディスクシークを増やすだけでメモリヒットでは無意味なので順次実行する。
    /// </remarks>
    public async Task<List<TextSegment>> ProcessTextSegmentsAsync(List<TextSegment> segments, VoiceRequest request, CancellationToken cancellationToken = default)
    {
        foreach (var segment in segments)
        {
            await ProcessSegmentAsync(segment, request, cancellationToken);
        }
        return segments;
    }

    private async Task ProcessSegmentAsync(TextSegment segment, VoiceRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        segment.SpeakerId = request.SpeakerId;

        var segmentRequest = request.WithText(segment.Text);
        var cachedAudio = await GetCachedAudioAsync(segmentRequest, cancellationToken);
        if (cachedAudio != null)
        {
            segment.AudioData = cachedAudio;
            segment.IsCached = true;
            _logger.LogDebug("セグメントがキャッシュから読み込まれました - テキスト: {Text}", segment.Text);
        }
        else
        {
            segment.AudioData = null;
            segment.IsCached = false;
            _logger.LogDebug("セグメントはキャッシュにありません - テキスト: {Text}", segment.Text);
        }
    }

    private Task<T> WithCacheErrorHandling<T>(CacheErrorMapper.OperationKind kind, string operation, string? cacheKey, Func<Task<T>> action)
        => CacheErrorMapper.WithMappingAsync(kind, operation, cacheKey, _disk.Directory, _logger, action);
}
