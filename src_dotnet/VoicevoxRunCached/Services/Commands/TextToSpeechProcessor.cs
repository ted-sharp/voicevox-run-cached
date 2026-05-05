using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services.Commands;

/// <summary>
/// テキスト読み上げのメイン処理フロー制御。
/// エンジン確認 → セグメント分割 → キャッシュ参照 → 不足分の合成 → 再生 → ファイル出力 までを担う。
/// </summary>
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

            var synthesisResult = await SynthesizeMissingSegmentsAsync(segments, request, noCache, cancellationToken);

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

            if (noPlay)
            {
                if (!String.IsNullOrWhiteSpace(outPath))
                {
                    await _audioExportService.ExportSegmentsAsync(segments, outPath, cancellationToken);
                }

                ConsoleHelper.WriteSuccess("Done (no-play mode)!", _logger);
                LogElapsedIfVerbose(verbose, totalStartTime);
                return hasPartialFailure ? 2 : 0;
            }

            var playbackResult = await ProcessAudioPlaybackAsync(segments, verbose, cancellationToken);
            if (playbackResult != 0)
            {
                return playbackResult;
            }

            if (!String.IsNullOrWhiteSpace(outPath))
            {
                await _audioExportService.ExportSegmentsAsync(segments, outPath, cancellationToken);
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

    private async Task<int> ProcessAudioPlaybackAsync(List<TextSegment> segments, bool verbose, CancellationToken cancellationToken)
    {
        try
        {
            var playbackStartTime = DateTime.UtcNow;

            await _audioPlayer.PlaySegmentsAsync(segments, _fillerManager, cancellationToken);

            if (verbose)
            {
                var elapsed = (DateTime.UtcNow - playbackStartTime).TotalMilliseconds;
                ConsoleHelper.WriteLine($"Audio playback completed in {elapsed:F1}ms", _logger);
            }

            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "音声再生処理中にエラーが発生しました");
            ConsoleHelper.WriteError($"Error: Failed to play audio: {ex.Message}", _logger);
            return 1;
        }
    }

    private async Task<SynthesisResult> SynthesizeMissingSegmentsAsync(List<TextSegment> segments, VoiceRequest originalRequest, bool noCache, CancellationToken cancellationToken)
    {
        var missingSegments = segments.Where(s => s.AudioData == null).ToList();
        if (missingSegments.Count == 0)
        {
            return SynthesisResult.Empty;
        }

        _logger.LogInformation("Synthesizing {Count} missing segments...", missingSegments.Count);

        await _apiClient.InitializeSpeakerAsync(originalRequest.SpeakerId, cancellationToken);

        int successCount = 0;
        int failureCount = 0;

        foreach (var segment in missingSegments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var segmentRequest = BuildSegmentRequest(segment.Text, originalRequest);
                var audioQuery = await _apiClient.GenerateAudioQueryAsync(segmentRequest, cancellationToken);
                var wavData = await _apiClient.SynthesizeAudioAsync(audioQuery, originalRequest.SpeakerId, cancellationToken);

                segment.AudioData = wavData;
                segment.IsCached = true; // 事前合成済みフラグを兼用 (再生側がフィラー判定に利用)
                successCount++;

                if (!noCache)
                {
                    await _cacheManager.SaveAudioCacheAsync(segmentRequest, wavData);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to synthesize segment: {Text}", segment.Text);
                failureCount++;
            }
        }

        return new SynthesisResult(missingSegments.Count, successCount, failureCount);
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

    private readonly record struct SynthesisResult(int AttemptedCount, int SuccessCount, int FailureCount)
    {
        public static SynthesisResult Empty { get; } = new(0, 0, 0);
    }
}
