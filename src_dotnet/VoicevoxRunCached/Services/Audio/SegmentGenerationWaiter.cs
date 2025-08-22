using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services;
using Serilog;

namespace VoicevoxRunCached.Services.Audio;

/// <summary>
/// セグメントの音声生成待機処理を行うクラス
/// </summary>
public class SegmentGenerationWaiter
{
    private readonly AudioProcessingChannel? _processingChannel;
    private readonly int _maxWaitTimeMs;

    public SegmentGenerationWaiter(AudioProcessingChannel? processingChannel = null, int maxWaitTimeMs = 30000)
    {
        this._processingChannel = processingChannel;
        this._maxWaitTimeMs = maxWaitTimeMs;
    }

    /// <summary>
    /// 未キャッシュセグメントの音声生成を待機し、必要に応じて生成を開始します
    /// </summary>
    /// <param name="segment">処理対象のセグメント</param>
    /// <param name="segmentIndex">セグメントのインデックス</param>
    /// <param name="cancellationToken">キャンセレーショントークン</param>
    /// <returns>音声生成の完了を表すTask</returns>
    public async Task WaitForSegmentGenerationAsync(TextSegment segment, int segmentIndex, CancellationToken cancellationToken = default)
    {
        if (segment.IsCached && segment.AudioData != null)
        {
            Log.Debug("セグメント {SegmentIndex} は既にキャッシュ済みです", segmentIndex + 1);
            return;
        }

        if (this._processingChannel != null)
        {
            await this.GenerateAudioWithChannelAsync(segment, segmentIndex, cancellationToken);
        }
        else
        {
            await this.WaitForFallbackGenerationAsync(segment, segmentIndex, cancellationToken);
        }
    }

    /// <summary>
    /// 音声処理チャンネルを使用して音声生成を行います
    /// </summary>
    /// <param name="segment">処理対象のセグメント</param>
    /// <param name="segmentIndex">セグメントのインデックス</param>
    /// <param name="cancellationToken">キャンセレーショントークン</param>
    /// <returns>音声生成の完了を表すTask</returns>
    private async Task GenerateAudioWithChannelAsync(TextSegment segment, int segmentIndex, CancellationToken cancellationToken)
    {
        if (this._processingChannel == null)
            throw new InvalidOperationException("Processing channel is not available");

        try
        {
            Log.Information("セグメント {SegmentIndex} の音声生成を開始します", segmentIndex + 1);

            var segmentRequest = new VoiceRequest
            {
                Text = segment.Text,
                SpeakerId = segment.SpeakerId ?? 1, // デフォルトフォールバック
                Speed = 1.0,
                Pitch = 0.0,
                Volume = 1.0
            };

            var result = await this._processingChannel.ProcessAudioAsync(segmentRequest, cancellationToken);
            if (result.Success && result.AudioData != null && result.AudioData.Length > 0)
            {
                segment.AudioData = result.AudioData;
                segment.IsCached = true;
                Log.Information("セグメント {SegmentIndex} の生成が完了しました (サイズ: {Size} bytes)",
                    segmentIndex + 1, result.AudioData.Length);
            }
            else
            {
                Log.Warning("セグメント {SegmentIndex} の生成に失敗しました: {Error}",
                    segmentIndex + 1, result.ErrorMessage ?? "Unknown error");
                throw new InvalidOperationException($"Failed to generate audio for segment {segmentIndex + 1}: {result.ErrorMessage}");
            }
        }
        catch (OperationCanceledException)
        {
            Log.Information("セグメント {SegmentIndex} の生成がキャンセルされました", segmentIndex + 1);
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "セグメント {SegmentIndex} の生成に失敗しました", segmentIndex + 1);
            throw new InvalidOperationException($"Failed to generate audio for segment {segmentIndex + 1}", ex);
        }
    }

    /// <summary>
    /// フォールバック処理としてセグメントの準備完了を待機します
    /// </summary>
    /// <param name="segment">処理対象のセグメント</param>
    /// <param name="segmentIndex">セグメントのインデックス</param>
    /// <param name="cancellationToken">キャンセレーショントークン</param>
    /// <returns>音声生成の完了を表すTask</returns>
    private async Task WaitForFallbackGenerationAsync(TextSegment segment, int segmentIndex, CancellationToken cancellationToken)
    {
        var waitStartTime = DateTime.UtcNow;
        Log.Information("フォールバック処理: セグメント {SegmentIndex} の準備完了を待機中...", segmentIndex + 1);

        while ((!segment.IsCached || segment.AudioData == null) &&
               (DateTime.UtcNow - waitStartTime).TotalMilliseconds < this._maxWaitTimeMs)
        {
            await Task.Delay(100, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }

        if ((DateTime.UtcNow - waitStartTime).TotalMilliseconds >= this._maxWaitTimeMs)
        {
            Log.Warning("セグメント {SegmentIndex} の待機がタイムアウトしました", segmentIndex + 1);
            throw new TimeoutException($"Segment {segmentIndex + 1} generation timeout after {this._maxWaitTimeMs}ms");
        }

        if (segment.AudioData == null)
        {
            Log.Warning("セグメント {SegmentIndex} の生成に失敗しました", segmentIndex + 1);
            throw new InvalidOperationException($"Segment {segmentIndex + 1} generation failed");
        }

        Log.Information("セグメント {SegmentIndex} の準備が完了しました (サイズ: {Size} bytes)",
            segmentIndex + 1, segment.AudioData.Length);
    }

    /// <summary>
    /// 複数セグメントの生成状況を一括でチェックします
    /// </summary>
    /// <param name="segments">チェック対象のセグメントリスト</param>
    /// <param name="cancellationToken">キャンセレーショントークン</param>
    /// <returns>生成状況の統計情報</returns>
    public Task<GenerationStatusStats> CheckGenerationStatusAsync(List<TextSegment> segments, CancellationToken cancellationToken = default)
    {
        var stats = new GenerationStatusStats
        {
            TotalSegments = segments.Count,
            CachedSegments = segments.Count(s => s.IsCached && s.AudioData != null),
            UncachedSegments = segments.Count(s => !s.IsCached || s.AudioData == null)
        };

        if (stats.UncachedSegments > 0)
        {
            Log.Information("生成状況: {Cached}/{Total} セグメントが準備完了",
                stats.CachedSegments, stats.TotalSegments);
        }

        return Task.FromResult(stats);
    }

    /// <summary>
    /// セグメントの生成完了を待機する際のタイムアウト設定を取得します
    /// </summary>
    /// <returns>タイムアウト設定（ミリ秒）</returns>
    public int GetMaxWaitTimeMs() => this._maxWaitTimeMs;

    /// <summary>
    /// 音声処理チャンネルの利用可能性を確認します
    /// </summary>
    /// <returns>利用可能な場合true</returns>
    public bool HasProcessingChannel() => this._processingChannel != null;
}

/// <summary>
/// 生成状況の統計情報
/// </summary>
public class GenerationStatusStats
{
    public int TotalSegments { get; set; }
    public int CachedSegments { get; set; }
    public int UncachedSegments { get; set; }
    public double CompletionRatio => this.TotalSegments > 0 ? (double)this.CachedSegments / this.TotalSegments : 0.0;
}
