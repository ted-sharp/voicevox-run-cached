using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services.Cache;

/// <summary>
/// 音声キャッシュの統合ファサード。ディスクストアと eviction を組み合わせて公開 API を提供する。
/// </summary>
/// <remarks>
/// 1 実行ごとに終了する CLI なのでメモリキャッシュは持たない (同一実行内で同じ文が繰り返される場合しか効かず、
/// ディスク読み込みも OS のファイルキャッシュで十分速い)。
/// </remarks>
public class AudioCacheManager
{
    private readonly DiskCacheStore _disk;
    private readonly CacheEvictionPolicy _eviction;
    private readonly ILogger _logger;
    private readonly int _mp3BitrateKbps;

    public AudioCacheManager(CacheSettings settings, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        _mp3BitrateKbps = settings.Mp3BitrateKbps;
        _disk = new DiskCacheStore(settings, _logger);
        _eviction = new CacheEvictionPolicy(_disk, settings, _logger);

        _logger.LogInformation("AudioCacheManager を初期化しました - キャッシュディレクトリ: {CacheDir}", _disk.Directory);
    }

    /// <summary>
    /// ディスクキャッシュから音声データ (MP3) を取得する。
    /// </summary>
    /// <remarks>
    /// 破損ファイル等の現実的な I/O エラーは <see cref="DiskCacheStore.LoadAsync"/> 内で
    /// 削除 + null 返却まで完結する。それ以外で例外が漏れた場合はバグなので surface させる。
    /// </remarks>
    public Task<byte[]?> GetCachedAudioAsync(VoiceRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _disk.LoadAsync(CacheKeyGenerator.Compute(request), cancellationToken);
    }

    /// <summary>
    /// 音声データをキャッシュに保存する。WAV はディスク保存前に MP3 に変換される。
    /// eviction はここでは行わない (保存ごとに全ファイル走査するのを避けるため、<see cref="RunMaintenanceAsync"/> でまとめて行う)。
    /// </summary>
    /// <remarks>
    /// 例外は wrapping せずそのまま伝播させる。呼び出し元 (SegmentProducer.SaveCacheBestEffortAsync) が
    /// best-effort で握って Warning ログを出すため、ここで CacheException に変換する意味は無い。
    /// </remarks>
    public async Task SaveAudioCacheAsync(VoiceRequest request, byte[] audioData, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cacheKey = CacheKeyGenerator.Compute(request);
        var mp3Data = AudioConversionUtility.ConvertWavToMp3(audioData, _mp3BitrateKbps);
        await _disk.SaveAsync(request, mp3Data, cacheKey, cancellationToken);

        _logger.LogDebug("キャッシュを保存しました: {CacheKey} - サイズ: {Size} bytes", cacheKey, mp3Data.Length);
    }

    /// <summary>
    /// 期限切れキャッシュの削除と、容量上限を超えた分の古い順削除をまとめて行う。
    /// </summary>
    /// <remarks>
    /// ディレクトリ全体を走査するため、セグメント保存ごとではなく 1 実行につき 1 回だけ呼ぶこと
    /// (<see cref="SegmentProducer"/> が全保存完了後に呼ぶ)。
    /// </remarks>
    public async Task RunMaintenanceAsync(CancellationToken cancellationToken = default)
    {
        await _eviction.CleanupExpiredAsync(cancellationToken);
        await _eviction.CleanupByMaxSizeAsync(cancellationToken);
    }

    public Task ClearAllCacheAsync(CancellationToken cancellationToken = default)
    {
        return CacheErrorMapper.WithMappingAsync<object?>("キャッシュのクリア", null, _disk.Directory, _logger, () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var file in _disk.GetAudioFiles())
            {
                _disk.Delete(Path.GetFileNameWithoutExtension(file));
            }
            _logger.LogInformation("すべてのキャッシュをクリアしました");
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
            _logger.LogDebug("セグメントはキャッシュにありません - テキスト: {Text}", segment.Text);
        }
    }
}
