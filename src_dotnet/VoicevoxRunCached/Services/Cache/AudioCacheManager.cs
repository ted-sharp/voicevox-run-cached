using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services.Cache;

/// <summary>
/// 音声キャッシュの統合ファサード。メモリ・ディスクストア、eviction、統計収集を組み合わせて公開 API を提供する。
/// </summary>
public class AudioCacheManager : IDisposable
{
    private readonly MemoryCacheStore _memory;
    private readonly DiskCacheStore _disk;
    private readonly CacheEvictionPolicy _eviction;
    private readonly CacheSettings _settings;
    private readonly ILogger _logger;

    public AudioCacheManager(CacheSettings settings, IMemoryCache memoryCache, ILogger? logger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        ArgumentNullException.ThrowIfNull(memoryCache);
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        _memory = new MemoryCacheStore(memoryCache, TimeSpan.FromDays(settings.ExpirationDays));
        _disk = new DiskCacheStore(settings, _logger);
        _eviction = new CacheEvictionPolicy(_disk, settings, _logger);

        _logger.LogInformation("AudioCacheManager を初期化しました - キャッシュディレクトリ: {CacheDir}, メモリキャッシュ: 有効, 最大サイズ: {MaxSizeMB}MB",
            _disk.Directory, settings.MemoryCacheSizeMb);
    }

    public void Dispose()
    {
        // IMemoryCache は DI コンテナが管理する singleton なので、ここでは触らない。
        // SaveAudioCacheAsync が同期的に eviction まで完了するため走行中タスクの追跡も不要。
        // 互換のため IDisposable は残すが本体は no-op。
    }

    /// <summary>
    /// キャッシュから音声データを取得する。メモリ→ディスクの順で fallback。
    /// </summary>
    /// <remarks>
    /// 読み込み I/O エラー (破損ファイル、権限不足等) は warning ログを残して null を返す。
    /// 呼び出し側は「ヒットしなかった」と同じ扱いになり API へ fallback する。
    /// (キャッシュ層の障害でユーザー操作が破綻しないことを優先する)
    /// </remarks>
    public async Task<byte[]?> GetCachedAudioAsync(VoiceRequest request, CancellationToken cancellationToken = default)
    {
        var cacheKey = CacheKeyGenerator.Compute(request);
        try
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
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "キャッシュ読み込みに失敗 (API fallback します): {CacheKey}", cacheKey);
            return null;
        }
    }

    /// <summary>
    /// 音声データをキャッシュに保存する。WAV はディスク保存前に MP3 に変換される。
    /// 容量超過時は同じ呼び出し内で eviction を走らせるため、戻り Task の完了は永続状態の確定を意味する。
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

            // size 超過時のみ古いファイルを削除 (通常はノーオペで安価)。
            // 旧実装は fire-and-forget で Dispose 時に追跡していたが、producer 側で WhenAll する設計と冗長だったため同期化。
            await _eviction.CleanupByMaxSizeAsync(cancellationToken);
            return null;
        });
    }

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
