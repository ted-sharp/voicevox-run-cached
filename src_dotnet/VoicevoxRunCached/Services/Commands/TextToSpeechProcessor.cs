using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services.Commands;

/// <summary>
/// テキスト読み上げのメイン処理フロー制御を行うクラス
/// </summary>
public class TextToSpeechProcessor
{
    private readonly AudioCacheManager _cacheManager;
    private readonly AudioExportService _audioExportService;
    private readonly AudioPlayer _audioPlayer;
    private readonly EngineCoordinator _engineCoordinator;
    private readonly FillerManager _fillerManager;
    private readonly ILogger _logger;
    private readonly SegmentProcessor _segmentProcessor;
    private readonly VoiceVoxApiClient _apiClient;

    public TextToSpeechProcessor(
        EngineCoordinator engineCoordinator,
        SegmentProcessor segmentProcessor,
        AudioExportService audioExportService,
        AudioPlayer audioPlayer,
        AudioCacheManager cacheManager,
        VoiceVoxApiClient apiClient,
        FillerManager fillerManager,
        ILogger logger)
    {
        _engineCoordinator = engineCoordinator ?? throw new ArgumentNullException(nameof(engineCoordinator));
        _segmentProcessor = segmentProcessor ?? throw new ArgumentNullException(nameof(segmentProcessor));
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
            if (!await EnsureEngineAvailableAsync(verbose, cancellationToken))
            {
                return 1;
            }

            // セグメント処理（常に実行）
            var segments = await _segmentProcessor.ProcessSegmentsAsync(request, noCache, cancellationToken);
            if (segments.Count == 0)
            {
                ConsoleHelper.WriteError("Error: No segments to process", _logger);
                return 1;
            }

            // キャッシュのみモードの場合、キャッシュされていないセグメントがあるかチェック
            if (cacheOnly)
            {
                var uncachedSegments = segments.Where(s => !s.IsCached).ToList();
                if (uncachedSegments.Count > 0)
                {
                    ConsoleHelper.WriteWarning($"Warning: {uncachedSegments.Count} segments are not cached", _logger);
                    return 1;
                }
            }

            // キャッシュにないセグメントを合成する（常に実行）
            var synthesisResult = await SynthesizeMissingSegmentsAsync(segments, request, noCache, cancellationToken);

            // 全セグメント失敗時は再生に進まず即エラー終了
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

            // 部分失敗時は警告を出して処理を続行 (キャッシュ済みセグメントだけでも再生する)
            bool hasPartialFailure = synthesisResult.FailureCount > 0;
            if (hasPartialFailure)
            {
                ConsoleHelper.WriteWarning(
                    $"Warning: {synthesisResult.FailureCount}/{synthesisResult.AttemptedCount} segments failed to synthesize",
                    _logger);
            }

            // --no-playが指定されている場合、再生をスキップ
            if (noPlay)
            {
                if (!String.IsNullOrWhiteSpace(outPath))
                {
                    await _audioExportService.ExportSegmentsAsync(segments, outPath, cancellationToken);
                }

                ConsoleHelper.WriteSuccess("Done (no-play mode)!", _logger);
                if (verbose)
                {
                    ConsoleHelper.WriteLine($"Total execution time: {(DateTime.UtcNow - totalStartTime).TotalMilliseconds:F1}ms", _logger);
                }
                return hasPartialFailure ? 2 : 0;
            }

            // 音声再生処理（フィラー機能付き）
            var playbackResult = await ProcessAudioPlaybackAsync(segments, verbose, cancellationToken);
            if (playbackResult != 0)
            {
                return playbackResult;
            }

            // ファイル出力（--out指定時、再生後）
            if (!String.IsNullOrWhiteSpace(outPath))
            {
                await _audioExportService.ExportSegmentsAsync(segments, outPath, cancellationToken);
            }

            if (verbose)
            {
                ConsoleHelper.WriteLine($"Total execution time: {(DateTime.UtcNow - totalStartTime).TotalMilliseconds:F1}ms", _logger);
            }

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

    private async Task<bool> EnsureEngineAvailableAsync(bool verbose, CancellationToken cancellationToken)
    {
        if (!await _engineCoordinator.EnsureEngineRunningAsync(cancellationToken))
        {
            ConsoleHelper.WriteError("Error: VOICEVOX engine is not available", _logger);
            return false;
        }

        if (verbose)
        {
            var engineStatus = await _engineCoordinator.GetEngineStatusAsync();
            ConsoleHelper.WriteLine($"Engine check completed at {engineStatus.LastChecked:HH:mm:ss}", _logger);
        }

        return true;
    }

    private async Task<int> ProcessAudioPlaybackAsync(List<TextSegment> segments, bool verbose, CancellationToken cancellationToken)
    {
        try
        {
            var playbackStartTime = DateTime.UtcNow;

            await _audioPlayer.PlaySegmentsAsync(segments, _fillerManager, cancellationToken);

            var playbackElapsed = (DateTime.UtcNow - playbackStartTime).TotalMilliseconds;
            if (verbose)
            {
                ConsoleHelper.WriteLine($"Audio playback completed in {playbackElapsed:F1}ms", _logger);
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
                var segmentRequest = new VoiceRequest
                {
                    Text = segment.Text,
                    SpeakerId = originalRequest.SpeakerId,
                    Speed = originalRequest.Speed,
                    Pitch = originalRequest.Pitch,
                    Volume = originalRequest.Volume
                };

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

    private readonly record struct SynthesisResult(int AttemptedCount, int SuccessCount, int FailureCount)
    {
        public static SynthesisResult Empty { get; } = new(0, 0, 0);
    }
}
