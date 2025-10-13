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
    private readonly AudioExportService _audioExportService;
    private readonly EngineCoordinator _engineCoordinator;
    private readonly ILogger _logger;
    private readonly SegmentProcessor _segmentProcessor;
    private readonly AppSettings _settings;

    public TextToSpeechProcessor(AppSettings settings, ILogger logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _engineCoordinator = new EngineCoordinator(settings.VoiceVox, logger);
        _audioExportService = new AudioExportService(settings, logger);
        _segmentProcessor = new SegmentProcessor(settings, logger);
    }

    /// <summary>
    /// テキスト読み上げ処理を実行します
    /// </summary>
    /// <param name="request">音声リクエスト</param>
    /// <param name="noCache">キャッシュを使用しないフラグ</param>
    /// <param name="cacheOnly">キャッシュのみを使用するフラグ</param>
    /// <param name="verbose">詳細出力フラグ</param>
    /// <param name="outPath">出力ファイルパス</param>
    /// <param name="noPlay">再生しないフラグ</param>
    /// <param name="cancellationToken">キャンセレーショントークン</param>
    /// <returns>処理結果の終了コード</returns>
    public async Task<int> ProcessTextToSpeechAsync(VoiceRequest request, bool noCache, bool cacheOnly, bool verbose = false, string? outPath = null, bool noPlay = false, CancellationToken cancellationToken = default)
    {
        var totalStartTime = DateTime.UtcNow;

        try
        {
            if (!await EnsureEngineAvailableAsync(verbose, cancellationToken))
            {
                return 1;
            }

            var exportTask = StartExportTaskIfNeeded(request, outPath, cancellationToken);

            if (noPlay)
            {
                return await HandleNoPlayModeAsync(exportTask, verbose, totalStartTime);
            }

            var segments = await _segmentProcessor.ProcessSegmentsAsync(request, noCache, cancellationToken);
            if (segments.Count == 0)
            {
                ConsoleHelper.WriteError("Error: No segments to process", _logger);
                return 1;
            }

            await PlayAudioSegmentsAsync(segments, cancellationToken);
            await WaitForExportTaskAsync(exportTask);

            if (verbose)
            {
                ConsoleHelper.WriteLine($"Total execution time: {(DateTime.UtcNow - totalStartTime).TotalMilliseconds:F1}ms", _logger);
            }

            return 0;
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

    private Task StartExportTaskIfNeeded(VoiceRequest request, string? outPath, CancellationToken cancellationToken)
    {
        if (String.IsNullOrWhiteSpace(outPath))
        {
            return Task.CompletedTask;
        }

        return Task.Run(async () =>
        {
            try
            {
                await _audioExportService.ExportAudioAsync(request, outPath, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // キャンセルされた場合は何もしない
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "音声ファイルの出力に失敗しました");
            }
        }, cancellationToken);
    }

    private async Task<int> HandleNoPlayModeAsync(Task? exportTask, bool verbose, DateTime totalStartTime)
    {
        if (exportTask != null)
        {
            await exportTask;
        }

        ConsoleHelper.WriteSuccess("Done (no-play mode)!", _logger);

        if (verbose)
        {
            ConsoleHelper.WriteLine($"Total execution time: {(DateTime.UtcNow - totalStartTime).TotalMilliseconds:F1}ms", _logger);
        }

        return 0;
    }

    private async Task PlayAudioSegmentsAsync(List<TextSegment> segments, CancellationToken cancellationToken)
    {
        var formatDetector = new AudioFormatDetector();
        var playbackController = new AudioPlaybackController(_settings.Audio, formatDetector);

        foreach (var segment in segments)
        {
            if (segment.AudioData != null)
            {
                await playbackController.PlayAudioAsync(segment.AudioData, cancellationToken);
            }
        }
    }

    private async Task WaitForExportTaskAsync(Task? exportTask)
    {
        if (exportTask == null)
        {
            return;
        }

        try
        {
            await exportTask;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("音声ファイルの出力がキャンセルされました");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "音声ファイルの出力に失敗しました");
        }
    }
}
