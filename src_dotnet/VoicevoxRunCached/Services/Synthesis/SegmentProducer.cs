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
/// キャッシュ済みのセグメントは WAV にデコードして流す (<see cref="AudioCacheManager.DecodeOrDelete"/>)。デコードは再生と並行させるため
/// ここで行い、読めなければ削除済みなので未キャッシュとして合成し直す。
/// エンジンの準備 (起動確認・話者の初期化) は最初に合成が必要になったときに 1 回だけ行う。全部キャッシュから読めればエンジンに接続しない。
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

    /// <param name="segments">流すセグメント。キャッシュ済みのものは AudioData に Ogg Opus を持つ (WAV に置き換えて流す)</param>
    /// <param name="writer">再生側への channel</param>
    /// <param name="originalRequest">話者・パラメータ (キャッシュキーの元)</param>
    /// <param name="saveToCache">合成した音声をキャッシュに保存するか (<c>--no-cache</c> なら false)</param>
    /// <param name="prepareSynthesis">
    /// 最初に合成が必要になったときに 1 回だけ呼ぶ準備 (エンジンの起動確認・話者の確認と初期化)。ここで起きた例外はそのまま投げる。
    /// null なら合成しない (<c>--cache-only</c>)。そのとき読めなかったキャッシュは失敗として数える
    /// </param>
    /// <param name="cancellationToken">キャンセル</param>
    public async Task<SynthesisResult> ProduceAsync(
        List<TextSegment> segments,
        ChannelWriter<TextSegment> writer,
        VoiceRequest originalRequest,
        bool saveToCache,
        Func<CancellationToken, Task>? prepareSynthesis,
        CancellationToken cancellationToken)
    {
        var stats = new SegmentStats();
        var pendingCacheSaves = new List<Task>();
        var synthesizedByText = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var engineUnavailable = false;
        var synthesisPrepared = false;

        try
        {
            for (int i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];
                cancellationToken.ThrowIfCancellationRequested();

                if (IsReady(segment))
                {
                    var wavData = await DecodeCachedAsync(segment, originalRequest, cancellationToken);
                    if (wavData != null)
                    {
                        segment.AudioData = wavData;
                        await writer.WriteAsync(segment, cancellationToken);
                        continue;
                    }

                    // 読めないキャッシュ (削除済み)。未キャッシュとして合成し直す
                    segment.IsCached = false;
                    segment.AudioData = null;
                }

                // 同じ文が 2 回目以降に出てきたら、合成し直さずに 1 回目の音声を使う。合成し直すと API を無駄に呼ぶうえ、
                // 同じキーの保存が並行して同じ meta.json / .opus.tmp に書き込み、共有違反で失敗しうる
                // (話者・パラメータは 1 回の実行で共通なので、文が同じならキャッシュキーも同じ)
                if (synthesizedByText.TryGetValue(segment.Text, out var synthesizedWav))
                {
                    segment.AudioData = synthesizedWav;
                    stats.Attempted++;
                    stats.Success++;
                    await writer.WriteAsync(segment, cancellationToken);
                    continue;
                }

                if (engineUnavailable || prepareSynthesis == null)
                {
                    // 合成を中止した後・合成しない (--cache-only) ときの未キャッシュ分。再生側に流しても skip されるだけなので流さない
                    stats.Attempted++;
                    stats.Failure++;
                    continue;
                }

                if (!synthesisPrepared)
                {
                    await prepareSynthesis(cancellationToken);
                    synthesisPrepared = true;
                }

                var synth = await SynthesizeOneAsync(segment, originalRequest, stats, cancellationToken);
                if (synth.WavData != null)
                {
                    synthesizedByText[segment.Text] = synth.WavData;
                }

                // 合成失敗 (AudioData == null) でも channel には流す。再生側で skip ログを出す
                await writer.WriteAsync(segment, cancellationToken);

                if (saveToCache && synth.WavData != null && synth.Request != null)
                {
                    pendingCacheSaves.Add(SaveCacheBestEffortAsync(synth.Request, synth.WavData));
                }

                if (synth.EngineUnavailable)
                {
                    engineUnavailable = true;
                    var remaining = segments.Skip(i + 1).Count(s => !IsReady(s) && !synthesizedByText.ContainsKey(s.Text));
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
    /// キャッシュの音声を WAV にデコードする。読めなければ (削除して) null。
    /// </summary>
    /// <remarks>
    /// デコードは CPU で 1 文あたり 100ms 程度かかる同期処理なので thread pool で行う。channel への書き込みは同期で完了するため、
    /// ここで await しないと最初の await まで全セグメントのデコードが呼び出し元のスレッドで続き、再生の開始がその分遅れる。
    /// </remarks>
    private Task<byte[]?> DecodeCachedAsync(TextSegment segment, VoiceRequest originalRequest, CancellationToken cancellationToken)
    {
        var cachedAudio = segment.AudioData!;
        var segmentRequest = originalRequest.WithText(segment.Text);
        return Task.Run(() => _cacheManager.DecodeOrDelete(segmentRequest, cachedAudio), cancellationToken);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
