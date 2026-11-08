using Microsoft.Extensions.Logging;
using NAudio.Wave;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Constants;
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
    private readonly int _opusBitrateKbps;

    public AudioCacheManager(CacheSettings settings, ILogger logger, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _opusBitrateKbps = settings.OpusBitrateKbps;
        _disk = new DiskCacheStore(settings, _logger, clock);
        _eviction = new CacheEvictionPolicy(_disk, settings, _logger, clock);

        _logger.LogDebug("AudioCacheManager を初期化しました - キャッシュディレクトリ: {CacheDir}", _disk.Directory);
    }

    /// <summary>
    /// ディスクキャッシュから音声データ (Ogg Opus) を取得する。デコードはしない (<see cref="DecodeOrDelete"/>)。
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
    /// キャッシュから読んだ音声 (Ogg Opus) を WAV (<see cref="AudioConstants.OpusDecodeSampleRate"/> mono 16bit) にデコードする。
    /// 読めなければ (Ogg でない・デコードで例外・サンプルが 0 個) そのキャッシュを削除して null を返す。
    /// </summary>
    /// <param name="request">キャッシュキーの元 (話者・パラメータ込み)</param>
    /// <param name="cachedAudio"><see cref="GetCachedAudioAsync"/> が返した音声</param>
    /// <remarks>
    /// 呼び出し元 (<see cref="SegmentProducer"/>) は null なら合成し直して保存し直すので、壊れたキャッシュがあってもその場で直る。
    /// 再生時まで読めないことに気づかないと、その文を含む読み上げが <c>--refresh</c> / <c>--clear</c> するまで毎回失敗するため。
    /// 途中で切れたファイル (前半だけ正しく読めるもの) は、デコードできた分だけ鳴るので検出しない。
    /// デコードは 1 文あたり 100ms 程度かかる (Concentus) ので、再生と並行して呼ぶこと
    /// (読み込み時にまとめて行うと、全文キャッシュ済みでも鳴り始めがその分遅れる)。
    /// </remarks>
    public byte[]? DecodeOrDelete(VoiceRequest request, byte[] cachedAudio)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(cachedAudio);

        byte[]? wavData = null;
        Exception? decodeError = null;
        try
        {
            wavData = DecodeIfPlayable(cachedAudio);
        }
        catch (Exception ex)
        {
            decodeError = ex;
        }
        if (wavData != null)
        {
            return wavData;
        }

        var cacheKey = CacheKeyGenerator.Compute(request);
        _logger.LogWarning(decodeError, "キャッシュの音声を読めないため削除します: {CacheKey} ({Text})", cacheKey, request.Text);
        _disk.Delete(cacheKey);
        return null;
    }

    /// <returns>デコードした WAV。Ogg でない・サンプルが 0 個なら null</returns>
    private static byte[]? DecodeIfPlayable(byte[] opusData)
    {
        if (AudioFormatDetector.DetectFormat(opusData) != AudioFormat.Opus)
        {
            return null;
        }

        var wavData = OpusCodec.DecodeToWav(opusData);
        using var reader = new WaveFileReader(new MemoryStream(wavData));
        return reader.Length > 0 ? wavData : null;
    }

    /// <summary>
    /// 音声データをキャッシュに保存する。WAV はディスク保存前に Ogg Opus に変換される。
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
        // エンコードは CPU バウンドの同期処理。呼び出し元 (SegmentProducer) は fire-and-forget で次の API 呼び出しに
        // 進みたいので、最初の await より前に同期で走らせず thread pool に逃がす
        var opusData = await Task.Run(() => OpusCodec.EncodeWav(audioData, _opusBitrateKbps), cancellationToken);
        await _disk.SaveAsync(request, opusData, cacheKey, cancellationToken);

        _logger.LogDebug("キャッシュを保存しました: {CacheKey} - サイズ: {Size} bytes (WAV {WavSize} bytes)", cacheKey, opusData.Length, audioData.Length);
    }

    /// <summary>
    /// 旧形式 (MP3)・書き込みの途中で残った一時ファイルの削除、期限切れキャッシュの削除、
    /// 容量上限を超えた分の古い順削除をまとめて行う。
    /// </summary>
    /// <remarks>
    /// ディレクトリ全体を走査するため、セグメント保存ごとではなく 1 実行につき 1 回だけ呼ぶこと
    /// (<see cref="SegmentProducer"/> が全保存完了後に呼ぶ)。
    /// </remarks>
    public async Task RunMaintenanceAsync(CancellationToken cancellationToken = default)
    {
        _disk.DeleteLegacyFiles();
        _disk.DeleteTemporaryFiles();
        await _eviction.CleanupExpiredAsync(cancellationToken);
        await _eviction.CleanupByMaxSizeAsync(cancellationToken);
    }

    public Task ClearAllCacheAsync(CancellationToken cancellationToken = default)
    {
        CacheErrorMapper.WithMapping("キャッシュのクリア", null, _disk.Directory, _logger, () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deleted = _disk.DeleteAll();
            _logger.LogInformation("すべてのキャッシュをクリアしました - 削除ファイル数: {Count}", deleted);
        });
        return Task.CompletedTask;
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
