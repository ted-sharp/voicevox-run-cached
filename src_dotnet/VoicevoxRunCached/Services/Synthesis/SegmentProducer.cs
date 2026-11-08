using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services.Synthesis;

/// <summary>
/// セグメントを順次合成して <see cref="Channel{T}"/> に流す producer。
/// </summary>
/// <remarks>
/// VOICEVOX API の直列性は <see cref="ProduceAsync"/> 内のループで 1 件ずつ await することで担保している。
/// 順序契約 (フィラー誤発火を避けるため厳守):
///   1. 全セグメントの write 完了 (または例外) → writer.TryComplete() で再生側を即座に解放
///   2. その後に pending cache save を WhenAll で待つ
///   3. 新規保存があれば、最後に 1 回だけキャッシュ eviction (期限切れ + 容量上限) を行う
/// 例外パスでも 2. を必ず実行することで、producer 終了 = 全 cache save 完了を上位に保証する。
/// エンジンに届かない失敗 (<see cref="VoiceVoxApiClient.IsEngineUnavailable"/>) が起きたら、残りの未キャッシュ分は API を呼ばずに失敗として数える
/// (1 件ごとにタイムアウト (ConnectionTimeout) を待つと、その間プロセス間ロックを持ったまま後続の読み上げも待たせるため)。
/// キャッシュ済みのセグメントはそのまま流して再生する。
/// </remarks>
public class SegmentProducer
{
    private readonly VoiceVoxApiClient _apiClient;
    private readonly AudioCacheManager _cacheManager;
    private readonly ILogger _logger;

    public SegmentProducer(VoiceVoxApiClient apiClient, AudioCacheManager cacheManager, ILogger logger)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <param name="saveToCache">合成した音声をキャッシュに保存するか (<c>--no-cache</c> なら false)</param>
    public async Task<SynthesisResult> ProduceAsync(
        List<TextSegment> segments,
        ChannelWriter<TextSegment> writer,
        VoiceRequest originalRequest,
        bool saveToCache,
        CancellationToken cancellationToken)
    {
        var stats = new SegmentStats();
        var pendingCacheSaves = new List<Task>();
        var engineUnavailable = false;

        try
        {
            for (int i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];
                cancellationToken.ThrowIfCancellationRequested();

                if (IsReady(segment))
                {
                    await writer.WriteAsync(segment, cancellationToken);
                    continue;
                }

                if (engineUnavailable)
                {
                    // 合成を中止した後の未キャッシュ分。再生側に流しても skip されるだけなので流さない
                    stats.Attempted++;
                    stats.Failure++;
                    continue;
                }

                var synth = await SynthesizeOneAsync(segment, originalRequest, stats, cancellationToken);

                // 合成失敗 (AudioData == null) でも channel には流す。再生側で skip ログを出す
                await writer.WriteAsync(segment, cancellationToken);

                if (saveToCache && synth.WavData != null && synth.Request != null)
                {
                    pendingCacheSaves.Add(SaveCacheBestEffortAsync(synth.Request, synth.WavData));
                }

                if (synth.EngineUnavailable)
                {
                    engineUnavailable = true;
                    var remaining = segments.Skip(i + 1).Count(s => !IsReady(s));
                    if (remaining > 0)
                    {
                        _logger.LogWarning("エンジンに接続できない・応答しないため、残り {Count} セグメントの合成を中止します", remaining);
                    }
                }
            }
        }
        finally
        {
            // 1. ディスク I/O より前に再生側を解放する。
            //    ここを WhenAll 後に置くと、最終セグメント再生後の WaitToReadAsync が
            //    FillerStartDelayMs を超えてフィラーを誤発火させる。
            writer.TryComplete();

            // 2. 例外パスでも cache save の完了を待つ (best-effort タスクなので例外は握る)。
            //    finally 内で await することで、上位は producer 終了 = 全保存完了を期待できる。
            if (pendingCacheSaves.Count > 0)
            {
                try
                { await Task.WhenAll(pendingCacheSaves); }
                catch { /* SaveCacheBestEffortAsync 内でログ済み */ }

                // 3. 新規保存があった実行でのみ、全保存完了後に 1 回だけ eviction を行う。
                //    再生側は 1. で解放済みなので、残りの再生と並走する。
                if (!cancellationToken.IsCancellationRequested)
                {
                    await RunCacheMaintenanceBestEffortAsync(cancellationToken);
                }
            }
        }

        return new SynthesisResult(stats.Attempted, stats.Success, stats.Failure);
    }

    /// <summary>
    /// 1 セグメント分の合成だけを担う。成功時は <see cref="TextSegment.AudioData"/> を埋めて
    /// 後段でキャッシュ保存できるよう <see cref="VoiceRequest"/> と WAV を返す。
    /// 例外は <see cref="OperationCanceledException"/> 以外を握り、失敗カウントを増やして null を返す。
    /// </summary>
    private async Task<SegmentSynthesis> SynthesizeOneAsync(
        TextSegment segment,
        VoiceRequest originalRequest,
        SegmentStats stats,
        CancellationToken cancellationToken)
    {
        stats.Attempted++;

        var segmentRequest = originalRequest.WithText(segment.Text);

        try
        {
            var audioQuery = await _apiClient.GenerateAudioQueryAsync(segmentRequest, cancellationToken);
            var wavData = await _apiClient.SynthesizeAudioAsync(audioQuery, originalRequest.SpeakerId, cancellationToken);

            // IsCached はキャッシュヒット判定に絞り、合成成功は AudioData != null で表す。
            // 再生側 (AudioPlayer) は AudioData を直接見ているのでフラグ更新は不要。
            segment.AudioData = wavData;
            stats.Success++;
            return new SegmentSynthesis(segmentRequest, wavData);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to synthesize segment: {Text}", segment.Text);
            stats.Failure++;
            // segment.AudioData は null のまま — 再生側で skip される
            return new SegmentSynthesis(null, null, VoiceVoxApiClient.IsEngineUnavailable(ex));
        }
    }

    /// <summary>
    /// 再生できる音声を既に持っている (キャッシュヒットした) セグメントか。
    /// </summary>
    private static bool IsReady(TextSegment segment) => segment.IsCached && segment.AudioData != null;

    private async Task SaveCacheBestEffortAsync(VoiceRequest request, byte[] wavData)
    {
        try
        {
            await _cacheManager.SaveAudioCacheAsync(request, wavData);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "キャッシュ保存に失敗しました (best-effort、再生は継続)");
        }
    }

    private async Task RunCacheMaintenanceBestEffortAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _cacheManager.RunMaintenanceAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C 等。eviction は次回実行で行えばよい
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "キャッシュの eviction に失敗しました (best-effort、再生は継続)");
        }
    }

    private readonly record struct SegmentSynthesis(VoiceRequest? Request, byte[]? WavData, bool EngineUnavailable = false);

    private sealed class SegmentStats
    {
        public int Attempted;
        public int Success;
        public int Failure;
    }
}

/// <summary>
/// セグメント (または <c>--init</c> のフィラー) 合成の集計結果。
/// </summary>
public readonly record struct SynthesisResult(int AttemptedCount, int SuccessCount, int FailureCount);
