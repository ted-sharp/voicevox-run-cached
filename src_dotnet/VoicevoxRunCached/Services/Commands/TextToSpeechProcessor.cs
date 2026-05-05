using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services.Commands;

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

        try
        {
            if (!await _engineManager.EnsureEngineRunningAsync())
            {
                ConsoleHelper.WriteError("Error: VOICEVOX engine is not available", _logger);
                return 1;
            }

            var segments = await LoadSegmentsAsync(request, noCache, cancellationToken);
            if (segments.Count == 0)
            {
                ConsoleHelper.WriteError("Error: No segments to process", _logger);
                return 1;
            }

            if (cacheOnly)
            {
                var uncachedCount = segments.Count(s => !s.IsCached);
                if (uncachedCount > 0)
                {
                    ConsoleHelper.WriteWarning($"Warning: {uncachedCount} segments are not cached", _logger);
                    return 1;
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
        catch (OperationCanceledException)
        {
            _logger.LogInformation("テキスト読み上げ処理がキャンセルされました");
            throw new VoicevoxRunCachedException(
                ErrorCodes.General.OperationCancelled,
                "Text-to-speech processing was cancelled",
                "テキスト読み上げ処理がキャンセルされました。",
                null,
                "操作を再実行してください。"
            );
        }
        catch (VoicevoxRunCachedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "テキスト読み上げ処理中に予期しないエラーが発生しました");
            throw new VoicevoxRunCachedException(
                ErrorCodes.General.UnknownError,
                $"Unexpected error during text-to-speech processing: {ex.Message}",
                "テキスト読み上げ処理中に予期しないエラーが発生しました。",
                ex,
                "アプリケーションを再起動し、問題が続く場合はログを確認してください。"
            );
        }
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
    /// セグメントを順次合成して channel に流す。キャッシュ済みは即座に流すだけ。
    /// 各 API 呼び出しは直列。channel への書き込み後にキャッシュ保存を fire-and-forget して、次の API 呼び出しを遅らせない。
    /// </summary>
    private async Task<SynthesisResult> ProduceSegmentsAsync(
        List<TextSegment> segments,
        ChannelWriter<TextSegment> writer,
        VoiceRequest originalRequest,
        bool noCache,
        CancellationToken cancellationToken)
    {
        int attempted = 0;
        int successCount = 0;
        int failureCount = 0;
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

                attempted++;
                VoiceRequest? segmentRequest = null;
                byte[]? wavData = null;

                try
                {
                    segmentRequest = BuildSegmentRequest(segment.Text, originalRequest);
                    var audioQuery = await _apiClient.GenerateAudioQueryAsync(segmentRequest, cancellationToken);
                    wavData = await _apiClient.SynthesizeAudioAsync(audioQuery, originalRequest.SpeakerId, cancellationToken);

                    segment.AudioData = wavData;
                    segment.IsCached = true; // 再生側がスキップ判定に利用 (合成成功フラグを兼用)
                    successCount++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to synthesize segment: {Text}", segment.Text);
                    failureCount++;
                    // segment.AudioData は null のまま — 再生側で skip される
                }

                // 合成失敗 (AudioData == null) でも channel には流す。再生側で skip ログを出す
                await writer.WriteAsync(segment, cancellationToken);

                if (!noCache && wavData != null && segmentRequest != null)
                {
                    pendingCacheSaves.Add(SaveCacheBestEffortAsync(segmentRequest, wavData));
                }
            }

            if (pendingCacheSaves.Count > 0)
            {
                await Task.WhenAll(pendingCacheSaves);
            }
        }
        finally
        {
            writer.TryComplete();
        }

        return new SynthesisResult(attempted, successCount, failureCount);
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
}
