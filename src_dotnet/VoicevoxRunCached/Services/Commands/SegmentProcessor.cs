using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services.Commands;

/// <summary>
/// セグメント処理の詳細を行うクラス
/// </summary>
public class SegmentProcessor
{
    private readonly AudioCacheManager _cacheManager;
    private readonly ILogger _logger;
    private readonly TextSegmentProcessor _textProcessor;

    public SegmentProcessor(AudioCacheManager cacheManager, TextSegmentProcessor textProcessor, ILogger logger)
    {
        _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
        _textProcessor = textProcessor ?? throw new ArgumentNullException(nameof(textProcessor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<List<TextSegment>> ProcessSegmentsAsync(VoiceRequest request, bool noCache, CancellationToken cancellationToken = default)
    {
        var segmentStartTime = DateTime.UtcNow;

        try
        {
            ConsoleHelper.WriteLine("Processing segments...", _logger);

            var segments = await _textProcessor.ProcessTextAsync(request.Text, request.SpeakerId, cancellationToken);

            if (segments.Count == 0)
            {
                _logger.LogWarning("処理可能なセグメントがありません");
                return segments;
            }

            _logger.LogInformation("テキストを {SegmentCount} 個のセグメントに分割しました", segments.Count);

            if (!noCache)
            {
                await LoadCachedAudioDataAsync(segments, cancellationToken);
            }

            var elapsed = (DateTime.UtcNow - segmentStartTime).TotalMilliseconds;
            _logger.LogDebug("セグメント処理が完了しました: {Elapsed}ms", elapsed);

            return segments;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "セグメント処理中にエラーが発生しました");
            throw new InvalidOperationException("セグメント処理中にエラーが発生しました", ex);
        }
    }

    private async Task LoadCachedAudioDataAsync(List<TextSegment> segments, CancellationToken cancellationToken)
    {
        try
        {
            var cacheHitCount = 0;

            foreach (var segment in segments)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("キャッシュ読み込みがキャンセルされました");
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
                var cachedAudio = await _cacheManager.GetCachedAudioAsync(segmentRequest);
                if (cachedAudio != null)
                {
                    segment.AudioData = cachedAudio;
                    segment.IsCached = true;
                    cacheHitCount++;
                }
            }

            _logger.LogInformation("キャッシュヒット: {HitCount}/{TotalCount}", cacheHitCount, segments.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "キャッシュからの読み込み中にエラーが発生しました");
        }
    }

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

public class SegmentStatistics
{
    public int TotalSegments { get; set; }
    public int CachedSegments { get; set; }
    public int UncachedSegments { get; set; }
    public int TotalTextLength { get; set; }
    public double CacheHitRate { get; set; }
}
