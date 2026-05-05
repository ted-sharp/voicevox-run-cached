using Microsoft.Extensions.Caching.Memory;
using Serilog;
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
    private readonly CacheStatisticsCollector _statistics;
    private readonly CacheSettings _settings;
    private bool _disposed;

    public AudioCacheManager(CacheSettings settings, IMemoryCache memoryCache)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));

        _memory = new MemoryCacheStore(memoryCache, TimeSpan.FromDays(settings.ExpirationDays));
        _disk = new DiskCacheStore(settings);
        _eviction = new CacheEvictionPolicy(_disk, settings);
        _statistics = new CacheStatisticsCollector(_memory, _disk, settings);

        Log.Information("AudioCacheManager を初期化しました - キャッシュディレクトリ: {CacheDir}, メモリキャッシュ: 有効, 最大サイズ: {MaxSizeMB}MB",
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
                Log.Error(ex, "AudioCacheManagerの破棄中にエラーが発生しました");
            }
        }
        _disposed = true;
    }

    public static string ComputeCacheKey(VoiceRequest request) => CacheKeyGenerator.Compute(request);

    /// <summary>
    /// キャッシュから音声データを取得する。メモリ→ディスクの順で fallback。
    /// </summary>
    public async Task<byte[]?> GetCachedAudioAsync(VoiceRequest request)
    {
        var cacheKey = CacheKeyGenerator.Compute(request);

        try
        {
            if (_memory.TryGet(cacheKey, out byte[]? memoryCached) && memoryCached != null)
            {
                Log.Debug("メモリキャッシュヒット: {CacheKey} - サイズ: {Size} bytes", cacheKey, memoryCached.Length);
                return memoryCached;
            }

            var audioData = await _disk.LoadAsync(cacheKey);
            if (audioData != null)
            {
                _memory.RecordHit();
                _memory.Set(cacheKey, audioData);
                return audioData;
            }

            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Error(ex, "キャッシュファイルへのアクセス権限がありません: {CacheKey}", cacheKey);
            throw new CacheException(
                ErrorCodes.Cache.CachePermissionDenied,
                $"Access denied to cache file: {ex.Message}",
                "キャッシュファイルへのアクセス権限がありません。",
                cacheKey,
                _disk.Directory,
                "キャッシュディレクトリのアクセス権限を確認してください。"
            );
        }
        catch (IOException ex)
        {
            Log.Error(ex, "キャッシュファイルの読み込みに失敗しました: {CacheKey}", cacheKey);
            throw new CacheException(
                ErrorCodes.Cache.CacheReadError,
                $"Failed to read cache file: {ex.Message}",
                "キャッシュファイルの読み込みに失敗しました。",
                cacheKey,
                _disk.Directory,
                "キャッシュディレクトリの状態を確認し、必要に応じてキャッシュをクリアしてください。"
            );
        }
        catch (Exception ex)
        {
            Log.Error(ex, "キャッシュからの音声データ取得中に予期しないエラーが発生しました: {CacheKey}", cacheKey);
            throw new CacheException(
                ErrorCodes.General.UnknownError,
                $"Unexpected error while retrieving cached audio: {ex.Message}",
                "キャッシュからの音声データ取得中に予期しないエラーが発生しました。",
                cacheKey,
                _disk.Directory,
                "アプリケーションを再起動し、問題が続く場合はログを確認してください。"
            );
        }
    }

    /// <summary>
    /// 音声データをキャッシュに保存する。WAV はディスク保存前に MP3 に変換される。
    /// </summary>
    public async Task SaveAudioCacheAsync(VoiceRequest request, byte[] audioData)
    {
        var cacheKey = CacheKeyGenerator.Compute(request);

        try
        {
            var mp3Data = AudioConversionUtility.ConvertWavToMp3(audioData);
            await _disk.SaveAsync(request, mp3Data, cacheKey);
            _memory.Set(cacheKey, mp3Data);

            Log.Debug("キャッシュを保存しました: {CacheKey} - サイズ: {Size} bytes", cacheKey, mp3Data.Length);

            _ = _eviction.RunBackgroundSizeCleanupAsync();
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Error(ex, "キャッシュディレクトリへの書き込み権限がありません: {CacheKey}", cacheKey);
            throw new CacheException(
                ErrorCodes.Cache.CachePermissionDenied,
                $"Access denied to cache directory: {ex.Message}",
                "キャッシュディレクトリへの書き込み権限がありません。",
                cacheKey,
                _disk.Directory,
                "キャッシュディレクトリの書き込み権限を確認してください。"
            );
        }
        catch (IOException ex) when (ex.Message.Contains("There is not enough space"))
        {
            Log.Error(ex, "キャッシュディレクトリの容量が不足しています: {CacheKey}", cacheKey);
            throw new CacheException(
                ErrorCodes.Cache.CacheFull,
                $"Cache directory is full: {ex.Message}",
                "キャッシュディレクトリの容量が不足しています。",
                cacheKey,
                _disk.Directory,
                "古いキャッシュファイルを削除するか、キャッシュディレクトリの容量を増やしてください。"
            );
        }
        catch (IOException ex)
        {
            Log.Error(ex, "キャッシュファイルの書き込みに失敗しました: {CacheKey}", cacheKey);
            throw new CacheException(
                ErrorCodes.Cache.CacheWriteError,
                $"Failed to write cache file: {ex.Message}",
                "キャッシュファイルの書き込みに失敗しました。",
                cacheKey,
                _disk.Directory,
                "ディスクの空き容量とキャッシュディレクトリの状態を確認してください。"
            );
        }
        catch (Exception ex)
        {
            Log.Error(ex, "キャッシュの保存に失敗: {CacheKey}", cacheKey);
            throw new CacheException(
                ErrorCodes.Cache.CacheWriteError,
                $"Failed to save audio cache: {ex.Message}",
                "キャッシュの保存に失敗しました。",
                cacheKey,
                _disk.Directory,
                "アプリケーションを再起動し、問題が続く場合はログを確認してください。"
            );
        }
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

            Log.Information("すべてのキャッシュをクリアしました - メモリ・ディスク共にクリア済み");
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Error(ex, "キャッシュのクリア中にアクセス権限エラーが発生しました");
            throw new CacheException(
                ErrorCodes.Cache.CachePermissionDenied,
                $"Access denied while clearing cache: {ex.Message}",
                "キャッシュのクリア中にアクセス権限エラーが発生しました。",
                null,
                _disk.Directory,
                "キャッシュディレクトリのアクセス権限を確認してください。"
            );
        }
        catch (IOException ex)
        {
            Log.Error(ex, "キャッシュのクリア中にI/Oエラーが発生しました");
            throw new CacheException(
                ErrorCodes.Cache.CacheReadError,
                $"I/O error while clearing cache: {ex.Message}",
                "キャッシュのクリア中にI/Oエラーが発生しました。",
                null,
                _disk.Directory,
                "ディスクの状態とキャッシュディレクトリの権限を確認してください。"
            );
        }
        catch (Exception ex)
        {
            Log.Error(ex, "キャッシュのクリアに失敗しました");
            throw new CacheException(
                ErrorCodes.General.UnknownError,
                $"Failed to clear cache: {ex.Message}",
                "キャッシュのクリアに失敗しました。",
                null,
                _disk.Directory,
                "アプリケーションを再起動し、問題が続く場合はログを確認してください。"
            );
        }
    }

    public AudioCacheStatistics GetCacheStatistics() => _statistics.GetSummary();
    public DetailedCacheStatistics GetDetailedStatistics() => _statistics.GetDetailed();
    public CacheEfficiencyAnalysis AnalyzeCacheEfficiency() => _statistics.Analyze();

    /// <summary>
    /// 並行処理を使用した複数セグメントの処理。各セグメントはキャッシュからの取得を試み、ヒット時に AudioData を埋める。
    /// </summary>
    public async Task<List<TextSegment>> ProcessTextSegmentsAsync(List<TextSegment> segments, VoiceRequest request, CancellationToken cancellationToken = default)
    {
        var tasks = segments.Select(segment => ProcessSegmentAsync(segment, request, cancellationToken));
        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    private async Task<TextSegment> ProcessSegmentAsync(TextSegment segment, VoiceRequest request, CancellationToken cancellationToken)
    {
        try
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

            var cachedAudio = await GetCachedAudioAsync(segmentRequest);
            if (cachedAudio != null)
            {
                segment.AudioData = cachedAudio;
                segment.IsCached = true;
                Log.Debug("セグメントがキャッシュから読み込まれました - テキスト: {Text}", segment.Text);
            }
            else
            {
                segment.AudioData = null;
                segment.IsCached = false;
                Log.Debug("セグメントはキャッシュにありません - テキスト: {Text}", segment.Text);
            }

            return segment;
        }
        catch (OperationCanceledException)
        {
            Log.Debug("セグメント処理がキャンセルされました - テキスト: {Text}", segment.Text);
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "セグメント処理中にエラーが発生しました - テキスト: {Text}", segment.Text);
            return segment;
        }
    }
}
