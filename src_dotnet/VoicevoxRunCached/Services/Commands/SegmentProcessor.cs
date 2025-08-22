using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services;

namespace VoicevoxRunCached.Services.Commands;

/// <summary>
/// セグメント処理の詳細を行うクラス
/// </summary>
public class SegmentProcessor
{
    private readonly AppSettings _settings;
    private readonly ILogger _logger;

    public SegmentProcessor(AppSettings settings, ILogger logger)
    {
        this._settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this._logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// テキストをセグメントに分割して処理します
    /// </summary>
    /// <param name="request">音声リクエスト</param>
    /// <param name="noCache">キャッシュを使用しないフラグ</param>
    /// <param name="cancellationToken">キャンセレーショントークン</param>
    /// <returns>処理されたセグメントのリスト</returns>
    public async Task<List<TextSegment>> ProcessSegmentsAsync(VoiceRequest request, bool noCache, CancellationToken cancellationToken = default)
    {
        var segmentStartTime = DateTime.UtcNow;

        try
        {
            ConsoleHelper.WriteLine("Processing segments...", this._logger);

            var textProcessor = new TextSegmentProcessor();
            var segments = await textProcessor.ProcessTextAsync(request.Text, request.SpeakerId, cancellationToken);

            if (segments.Count == 0)
            {
                this._logger.LogWarning("処理可能なセグメントがありません");
                return segments;
            }

            this._logger.LogInformation("テキストを {SegmentCount} 個のセグメントに分割しました", segments.Count);

            // キャッシュを使用する場合、キャッシュから音声データを取得
            if (!noCache)
            {
                await this.LoadCachedAudioDataAsync(segments, cancellationToken);
            }

            var elapsed = (DateTime.UtcNow - segmentStartTime).TotalMilliseconds;
            this._logger.LogDebug("セグメント処理が完了しました: {Elapsed}ms", elapsed);

            return segments;
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "セグメント処理中にエラーが発生しました");
            throw;
        }
    }

    /// <summary>
    /// キャッシュから音声データを読み込みます
    /// </summary>
    /// <param name="segments">セグメントのリスト</param>
    /// <param name="cancellationToken">キャンセレーショントークン</param>
    /// <returns>キャッシュ読み込み完了を表すTask</returns>
    private async Task LoadCachedAudioDataAsync(List<TextSegment> segments, CancellationToken cancellationToken)
    {
        try
        {
            var cacheManager = new AudioCacheManager(this._settings.Cache);
            var cacheHitCount = 0;

            foreach (var segment in segments)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    this._logger.LogWarning("キャッシュ読み込みがキャンセルされました");
                    break;
                }

                var segmentRequest = new VoiceRequest
                {
                    Text = segment.Text,
                    SpeakerId = segment.SpeakerId ?? 1,
                    Speed = 1.0,
                    Pitch = 0.0,
                    Volume = 1.0
                };
                var cachedAudio = await cacheManager.GetCachedAudioAsync(segmentRequest);
                if (cachedAudio != null)
                {
                    segment.AudioData = cachedAudio;
                    segment.IsCached = true;
                    cacheHitCount++;
                }
            }

            this._logger.LogInformation("キャッシュヒット: {HitCount}/{TotalCount}", cacheHitCount, segments.Count);
        }
        catch (Exception ex)
        {
            this._logger.LogWarning(ex, "キャッシュからの読み込み中にエラーが発生しました");
        }
    }

    /// <summary>
    /// セグメントの統計情報を取得します
    /// </summary>
    /// <param name="segments">セグメントのリスト</param>
    /// <returns>セグメントの統計情報</returns>
    public SegmentStatistics GetSegmentStatistics(List<TextSegment> segments)
    {
        var totalSegments = segments.Count;
        var cachedSegments = segments.Count(s => s.IsCached);
        var uncachedSegments = totalSegments - cachedSegments;
        var totalTextLength = segments.Sum(s => s.Text.Length);

        return new SegmentStatistics
        {
            TotalSegments = totalSegments,
            CachedSegments = cachedSegments,
            UncachedSegments = uncachedSegments,
            TotalTextLength = totalTextLength,
            CacheHitRate = totalSegments > 0 ? (double)cachedSegments / totalSegments : 0.0
        };
    }
}

/// <summary>
/// セグメントの統計情報
/// </summary>
public class SegmentStatistics
{
    public int TotalSegments { get; set; }
    public int CachedSegments { get; set; }
    public int UncachedSegments { get; set; }
    public int TotalTextLength { get; set; }
    public double CacheHitRate { get; set; }
}
