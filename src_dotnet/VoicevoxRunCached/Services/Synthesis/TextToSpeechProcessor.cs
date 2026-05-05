using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services.Synthesis;

/// <summary>
/// テキスト読み上げのメイン処理フロー制御。
/// エンジン確認 → セグメント分割 → キャッシュ参照 → 不足分の合成と再生を <see cref="Channel{T}"/> 経由で並走 → ファイル出力 までを担う。
/// </summary>
/// <remarks>
/// Producer (合成側) と Consumer (再生側) を Channel で繋ぐことで、キャッシュ済みセグメントは即座に再生開始しつつ、
/// 未キャッシュ分はバックグラウンドで生成する。生成待ちの隙間は <see cref="FillerManager"/> 経由でフィラー音声が挿入される。
/// VOICEVOX API の直列性は producer 側 1 本の <c>foreach</c> で担保している。
/// </remarks>
public class TextToSpeechProcessor
{
    private readonly AudioCacheManager _cacheManager;
    private readonly AudioExportService _audioExportService;
    private readonly AudioPlayer _audioPlayer;
    private readonly VoiceVoxEngineManager _engineManager;
    private readonly FillerManager _fillerManager;
    private readonly ILogger _logger;
    private readonly TextSegmentProcessor _textProcessor;
    private readonly VoiceVoxApiClient _apiClient;

    public TextToSpeechProcessor(
        VoiceVoxEngineManager engineManager,
        TextSegmentProcessor textProcessor,
        AudioExportService audioExportService,
        AudioPlayer audioPlayer,
        AudioCacheManager cacheManager,
        VoiceVoxApiClient apiClient,
        FillerManager fillerManager,
        ILogger logger)
    {
        _engineManager = engineManager ?? throw new ArgumentNullException(nameof(engineManager));
        _textProcessor = textProcessor ?? throw new ArgumentNullException(nameof(textProcessor));
        _audioExportService = audioExportService ?? throw new ArgumentNullException(nameof(audioExportService));
        _audioPlayer = audioPlayer ?? throw new ArgumentNullException(nameof(audioPlayer));
        _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _fillerManager = fillerManager ?? throw new ArgumentNullException(nameof(fillerManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<int> ProcessTextToSpeechAsync(VoiceRequest request, bool noCache, bool cacheOnly, bool verbose = false, string? outPath = null, bool noPlay = false, CancellationToken cancellationToken = default)
    {
        var totalStartTime = DateTime.UtcNow;

        if (!await _engineManager.EnsureEngineRunningAsync(cancellationToken))
        {
            throw new VoicevoxRunCachedException(
                ErrorCodes.Engine.EngineNotAvailable,
                "VOICEVOX engine is not available",
                "VOICEVOXエンジンが利用できません。",
                null,
                "VOICEVOXエンジンが起動しているか、appsettings.json の EnginePath が正しいか確認してください。");
        }

        var segments = await LoadSegmentsAsync(request, noCache, cancellationToken);
        if (segments.Count == 0)
        {
            throw new VoicevoxRunCachedException(
                ErrorCodes.General.InvalidArguments,
                "No segments to process",
                "処理対象のセグメントがありません。",
                null,
                "テキストが空でないか確認してください。");
        }

        if (cacheOnly)
        {
            var uncachedCount = segments.Count(s => !s.IsCached);
            if (uncachedCount > 0)
            {
                throw new VoicevoxRunCachedException(
                    ErrorCodes.Cache.CacheReadError,
                    $"{uncachedCount} segments are not cached",
                    $"{uncachedCount} 個のセグメントがキャッシュに存在しません。",
                    null,
                    "--cache-only を外すか、事前に該当テキストを合成してキャッシュを作成してください。");
            }
        }

        var synthesisResult = await SynthesizeAndPlayAsync(segments, request, noCache, noPlay, verbose, cancellationToken);

        if (synthesisResult.AttemptedCount > 0 && synthesisResult.SuccessCount == 0)
        {
            throw new VoicevoxRunCachedException(
                ErrorCodes.Audio.AudioGenerationFailed,
                $"All {synthesisResult.AttemptedCount} segments failed to synthesize",
                "全てのセグメントの音声合成に失敗しました。",
                null,
                "VOICEVOXエンジンの状態とログを確認してください。"
            );
        }

        bool hasPartialFailure = synthesisResult.FailureCount > 0;
        if (hasPartialFailure)
        {
            ConsoleHelper.WriteWarning(
                $"Warning: {synthesisResult.FailureCount}/{synthesisResult.AttemptedCount} segments failed to synthesize",
                _logger);
        }

        if (!String.IsNullOrWhiteSpace(outPath))
        {
            await _audioExportService.ExportSegmentsAsync(segments, outPath, cancellationToken);
        }

        if (noPlay)
        {
            ConsoleHelper.WriteSuccess("Done (no-play mode)!", _logger);
        }

        LogElapsedIfVerbose(verbose, totalStartTime);
        return hasPartialFailure ? 2 : 0;
    }

    private async Task<List<TextSegment>> LoadSegmentsAsync(VoiceRequest request, bool noCache, CancellationToken cancellationToken)
    {
        ConsoleHelper.WriteLine("Processing segments...", _logger);

        var segments = await _textProcessor.ProcessTextAsync(request.Text, request.SpeakerId, cancellationToken);
        if (segments.Count == 0)
        {
            _logger.LogWarning("処理可能なセグメントがありません");
            return segments;
        }

        _logger.LogInformation("テキストを {SegmentCount} 個のセグメントに分割しました", segments.Count);

        if (noCache)
        {
            return segments;
        }

        // セグメントごとに request の Speed/Pitch/Volume を引き継いでキャッシュを参照する
        // (この値は cache key の一部なので、未指定値で問い合わせると永遠にミスヒットする)
        var resolved = await _cacheManager.ProcessTextSegmentsAsync(segments, request, cancellationToken);
        var hitCount = resolved.Count(s => s.IsCached);
        _logger.LogInformation("キャッシュヒット: {HitCount}/{TotalCount}", hitCount, resolved.Count);
        return resolved;
    }

    /// <summary>
    /// Producer (合成) と Consumer (再生) を <see cref="Channel{T}"/> で繋ぎ、両方が並走しながら処理する。
    /// </summary>
    private async Task<SynthesisResult> SynthesizeAndPlayAsync(
        List<TextSegment> segments,
        VoiceRequest request,
        bool noCache,
        bool noPlay,
        bool verbose,
        CancellationToken cancellationToken)
    {
        var anyMissing = segments.Any(s => !s.IsCached);
        if (anyMissing)
        {
            await _apiClient.InitializeSpeakerAsync(request.SpeakerId, cancellationToken);
        }

        var channel = Channel.CreateUnbounded<TextSegment>(new UnboundedChannelOptions
        {
            SingleWriter = true,
            SingleReader = true,
        });

        // 一方が落ちたらもう一方も止める。Linked CTS は finally でキャンセル伝播に使う。
        using var sharedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var producerTask = ProduceSegmentsAsync(segments, channel.Writer, request, noCache, sharedCts.Token);
        var playbackTask = noPlay
            ? Task.CompletedTask
            : RunPlaybackAsync(channel.Reader, verbose, sharedCts.Token);

        try
        {
            await Task.WhenAll(producerTask, playbackTask);
        }
        catch
        {
            // 片側の例外でもう一方が終わるよう CTS をキャンセルし、両 Task の終了を待ってから rethrow
            sharedCts.Cancel();
            try
            { await Task.WhenAll(producerTask, playbackTask); }
            catch
            { /* 二次例外は既出例外を上書きしないよう握る */ }
            throw;
        }

        return await producerTask;
    }

    private async Task RunPlaybackAsync(ChannelReader<TextSegment> reader, bool verbose, CancellationToken cancellationToken)
    {
        var playbackStartTime = DateTime.UtcNow;
        await _audioPlayer.PlayStreamingAsync(reader, _fillerManager, cancellationToken);

        if (verbose)
        {
            var elapsed = (DateTime.UtcNow - playbackStartTime).TotalMilliseconds;
            ConsoleHelper.WriteLine($"Audio playback completed in {elapsed:F1}ms", _logger);
        }
    }

    /// <summary>
    /// セグメントを順次処理して channel に流す producer。キャッシュ済みは即座に流すだけ、
    /// 未キャッシュは <see cref="SynthesizeOneAsync"/> に委譲して合成 → channel write → cache save の順で進める。
    /// </summary>
    /// <remarks>
    /// 順序契約 (フィラー誤発火を避けるため厳守):
    ///   1. 全セグメントの write 完了 (または例外) → writer.TryComplete() で再生側を即座に解放
    ///   2. その後に pendingCacheSaves を WhenAll で待つ
    /// 例外パスでも 2. を必ず実行することで、producer 終了 = 全 cache save 完了を上位に保証する。
    /// </remarks>
    private async Task<SynthesisResult> ProduceSegmentsAsync(
        List<TextSegment> segments,
        ChannelWriter<TextSegment> writer,
        VoiceRequest originalRequest,
        bool noCache,
        CancellationToken cancellationToken)
    {
        var stats = new SegmentStats();
        var pendingCacheSaves = new List<Task>();

        try
        {
            foreach (var segment in segments)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (segment.IsCached && segment.AudioData != null)
                {
                    await writer.WriteAsync(segment, cancellationToken);
                    continue;
                }

                var synth = await SynthesizeOneAsync(segment, originalRequest, stats, cancellationToken);

                // 合成失敗 (AudioData == null) でも channel には流す。再生側で skip ログを出す
                await writer.WriteAsync(segment, cancellationToken);

                if (!noCache && synth.WavData != null && synth.Request != null)
                {
                    pendingCacheSaves.Add(SaveCacheBestEffortAsync(synth.Request, synth.WavData));
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

        var segmentRequest = BuildSegmentRequest(segment.Text, originalRequest);

        try
        {
            var audioQuery = await _apiClient.GenerateAudioQueryAsync(segmentRequest, cancellationToken);
            var wavData = await _apiClient.SynthesizeAudioAsync(audioQuery, originalRequest.SpeakerId, cancellationToken);

            segment.AudioData = wavData;
            segment.IsCached = true; // 再生側がスキップ判定に利用 (合成成功フラグを兼用)
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
            return new SegmentSynthesis(null, null);
        }
    }

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

    private static VoiceRequest BuildSegmentRequest(string text, VoiceRequest source) => new()
    {
        Text = text,
        SpeakerId = source.SpeakerId,
        Speed = source.Speed,
        Pitch = source.Pitch,
        Volume = source.Volume
    };

    private void LogElapsedIfVerbose(bool verbose, DateTime startedAt)
    {
        if (!verbose)
        {
            return;
        }
        ConsoleHelper.WriteLine($"Total execution time: {(DateTime.UtcNow - startedAt).TotalMilliseconds:F1}ms", _logger);
    }

    private readonly record struct SynthesisResult(int AttemptedCount, int SuccessCount, int FailureCount);

    /// <summary>
    /// 1 セグメントの合成結果。成功時は cache save 用に Request と WAV を持ち、失敗時は両方 null。
    /// </summary>
    private readonly record struct SegmentSynthesis(VoiceRequest? Request, byte[]? WavData);

    /// <summary>
    /// producer ループ全体の合成統計。可変なので class。
    /// </summary>
    private sealed class SegmentStats
    {
        public int Attempted;
        public int Success;
        public int Failure;
    }
}
