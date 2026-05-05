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
    /// <summary>
    /// I/O 例外発生時にエラーコードを切り分けるための操作種別。
    /// </summary>
    private enum CacheOperationKind
    {
        Read,
        Write,
        Clear
    }

    private readonly IMemoryCache _memoryCache;
    private readonly MemoryCacheStore _memory;
    private readonly DiskCacheStore _disk;
    private readonly CacheEvictionPolicy _eviction;
    private readonly CacheSettings _settings;
    private readonly ILogger _logger;
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
        return WithCacheErrorHandling(CacheOperationKind.Read, "キャッシュからの音声データ取得", cacheKey, async () =>
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
        return WithCacheErrorHandling<object?>(CacheOperationKind.Write, "キャッシュの保存", cacheKey, async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var mp3Data = AudioConversionUtility.ConvertWavToMp3(audioData);
            await _disk.SaveAsync(request, mp3Data, cacheKey, cancellationToken);
            _memory.Set(cacheKey, mp3Data);

            _logger.LogDebug("キャッシュを保存しました: {CacheKey} - サイズ: {Size} bytes", cacheKey, mp3Data.Length);

            _ = _eviction.RunBackgroundSizeCleanupAsync();
            return null;
        });
    }

    public Task CleanupExpiredCacheAsync(CancellationToken cancellationToken = default)
        => _eviction.CleanupExpiredAsync(cancellationToken);

    public void CleanupByMaxSize(CancellationToken cancellationToken = default)
        => _eviction.CleanupByMaxSize(cancellationToken);

    public void ClearAllCache()
    {
        try
        {
            _memory.Clear();

            foreach (var file in _disk.GetAudioFiles())
            {
                _disk.Delete(Path.GetFileNameWithoutExtension(file));
            }

            _logger.LogInformation("すべてのキャッシュをクリアしました - メモリ・ディスク共にクリア済み");
        }
        catch (UnauthorizedAccessException ex)
        {
            throw CreateCacheException(ErrorCodes.Cache.CachePermissionDenied,
                "Access denied while clearing cache", "キャッシュのクリア中にアクセス権限エラーが発生しました。",
                "キャッシュディレクトリのアクセス権限を確認してください。", null, ex);
        }
        catch (IOException ex)
        {
            throw CreateCacheException(ErrorCodes.Cache.CacheReadError,
                "I/O error while clearing cache", "キャッシュのクリア中にI/Oエラーが発生しました。",
                "ディスクの状態とキャッシュディレクトリの権限を確認してください。", null, ex);
        }
        catch (Exception ex)
        {
            throw CreateCacheException(ErrorCodes.General.UnknownError,
                "Failed to clear cache", "キャッシュのクリアに失敗しました。",
                "アプリケーションを再起動し、問題が続く場合はログを確認してください。", null, ex);
        }
    }

    /// <summary>
    /// 複数セグメントを順次処理する。各セグメントはキャッシュからの取得を試み、ヒット時に AudioData を埋める。
    /// </summary>
    /// <remarks>
    /// 並列化は I/O 集中時にディスクシークを増やすだけでメモリヒットでは無意味。
    /// 順次にすることで進捗ログも読みやすくなる。
    /// </remarks>
    public async Task<List<TextSegment>> ProcessTextSegmentsAsync(List<TextSegment> segments, VoiceRequest request, CancellationToken cancellationToken = default)
    {
        var results = new List<TextSegment>(segments.Count);
        foreach (var segment in segments)
        {
            results.Add(await ProcessSegmentAsync(segment, request, cancellationToken));
        }
        return results;
    }

    private async Task<TextSegment> ProcessSegmentAsync(TextSegment segment, VoiceRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        segment.SpeakerId = request.SpeakerId;

        var segmentRequest = new VoiceRequest
        {
            Text = segment.Text,
            SpeakerId = request.SpeakerId,
            Speed = request.Speed,
            Pitch = request.Pitch,
            Volume = request.Volume
        };

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

        return segment;
    }

    /// <summary>
    /// 同一形のキャッシュ I/O 例外ハンドリング (UnauthorizedAccessException / IOException / その他)。
    /// </summary>
    private async Task<T> WithCacheErrorHandling<T>(CacheOperationKind kind, string operation, string cacheKey, Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (CacheException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw CreateCacheException(ErrorCodes.Cache.CachePermissionDenied,
                $"Access denied during {operation}",
                $"{operation}のアクセス権限がありません。",
                "キャッシュディレクトリのアクセス権限を確認してください。", cacheKey, ex);
        }
        catch (IOException ex) when (ex.Message.Contains("There is not enough space"))
        {
            throw CreateCacheException(ErrorCodes.Cache.CacheFull,
                $"Cache directory is full during {operation}",
                "キャッシュディレクトリの容量が不足しています。",
                "古いキャッシュファイルを削除するか、キャッシュディレクトリの容量を増やしてください。", cacheKey, ex);
        }
        catch (IOException ex)
        {
            var code = kind == CacheOperationKind.Write
                ? ErrorCodes.Cache.CacheWriteError
                : ErrorCodes.Cache.CacheReadError;
            throw CreateCacheException(code,
                $"I/O error during {operation}: {ex.Message}",
                $"{operation}のファイル操作に失敗しました。",
                "ディスクの空き容量とキャッシュディレクトリの状態を確認してください。", cacheKey, ex);
        }
        catch (Exception ex)
        {
            throw CreateCacheException(ErrorCodes.General.UnknownError,
                $"Unexpected error during {operation}: {ex.Message}",
                $"{operation}中に予期しないエラーが発生しました。",
                "アプリケーションを再起動し、問題が続く場合はログを確認してください。", cacheKey, ex);
        }
    }

    private CacheException CreateCacheException(string errorCode, string message, string userMessage, string solution, string? cacheKey, Exception inner)
    {
        _logger.LogError(inner, "{Message}: {CacheKey}", message, cacheKey ?? "-");
        return new CacheException(errorCode, message, userMessage, inner, cacheKey, _disk.Directory, solution);
    }
}
