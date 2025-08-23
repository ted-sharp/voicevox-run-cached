using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services;
using VoicevoxRunCached.Exceptions;

namespace VoicevoxRunCached.Services.Commands;

/// <summary>
/// テキスト読み上げのメイン処理フロー制御を行うクラス
/// </summary>
public class TextToSpeechProcessor
{
    private readonly AppSettings _settings;
    private readonly ILogger _logger;
    private readonly EngineCoordinator _engineCoordinator;
    private readonly AudioExportService _audioExportService;
    private readonly SegmentProcessor _segmentProcessor;

    public TextToSpeechProcessor(AppSettings settings, ILogger logger)
    {
        this._settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this._logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this._engineCoordinator = new EngineCoordinator(settings.VoiceVox, logger);
        this._audioExportService = new AudioExportService(settings, logger);
        this._segmentProcessor = new SegmentProcessor(settings, logger);
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
            // VOICEVOXエンジンが動作していることを確認
            if (!await this._engineCoordinator.EnsureEngineRunningAsync(cancellationToken))
            {
                ConsoleHelper.WriteError("Error: VOICEVOX engine is not available", this._logger);
                return 1;
            }

            if (verbose)
            {
                var engineStatus = await this._engineCoordinator.GetEngineStatusAsync();
                ConsoleHelper.WriteLine($"Engine check completed at {engineStatus.LastChecked:HH:mm:ss}", this._logger);
            }

            // 出力ファイルが指定されている場合、バックグラウンドエクスポートタスクを開始
            Task? exportTask = null;
            if (!String.IsNullOrWhiteSpace(outPath))
            {
                exportTask = Task.Run(async () =>
                {
                    try
                    {
                        await this._audioExportService.ExportAudioAsync(request, outPath!, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        // キャンセルされた場合は何もしない
                    }
                    catch (Exception ex)
                    {
                        this._logger.LogWarning(ex, "音声ファイルの出力に失敗しました");
                    }
                }, cancellationToken);
            }

            if (noPlay)
            {
                if (exportTask != null)
                {
                    await exportTask;
                }
                ConsoleHelper.WriteSuccess("Done (no-play mode)!", this._logger);
                if (verbose)
                {
                    ConsoleHelper.WriteLine($"Total execution time: {(DateTime.UtcNow - totalStartTime).TotalMilliseconds:F1}ms", this._logger);
                }
                return 0;
            }

            // セグメント処理
            var segments = await this._segmentProcessor.ProcessSegmentsAsync(request, noCache, cancellationToken);
            if (segments.Count == 0)
            {
                ConsoleHelper.WriteError("Error: No segments to process", this._logger);
                return 1;
            }

            // 音声再生処理
            var formatDetector = new AudioFormatDetector();
            var playbackController = new AudioPlaybackController(this._settings.Audio, formatDetector);

            // 各セグメントを順次再生
            foreach (var segment in segments)
            {
                if (segment.AudioData != null)
                {
                    await playbackController.PlayAudioAsync(segment.AudioData, cancellationToken);
                }
            }

            // エクスポートタスクの完了を待機
            if (exportTask != null)
            {
                try
                {
                    await exportTask;
                }
                catch (OperationCanceledException)
                {
                    this._logger.LogInformation("音声ファイルの出力がキャンセルされました");
                }
                catch (Exception ex)
                {
                    this._logger.LogWarning(ex, "音声ファイルの出力に失敗しました");
                }
            }

            if (verbose)
            {
                ConsoleHelper.WriteLine($"Total execution time: {(DateTime.UtcNow - totalStartTime).TotalMilliseconds:F1}ms", this._logger);
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            this._logger.LogInformation("テキスト読み上げ処理がキャンセルされました");
            throw new VoicevoxRunCachedException(
                ErrorCodes.General.OPERATION_CANCELLED,
                "Text-to-speech processing was cancelled",
                "テキスト読み上げ処理がキャンセルされました。",
                null,
                "操作を再実行してください。"
            );
        }
        catch (VoicevoxRunCachedException)
        {
            // 既に適切に処理された例外は再スロー
            throw;
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "テキスト読み上げ処理中に予期しないエラーが発生しました");
            throw new VoicevoxRunCachedException(
                ErrorCodes.General.UNKNOWN_ERROR,
                $"Unexpected error during text-to-speech processing: {ex.Message}",
                "テキスト読み上げ処理中に予期しないエラーが発生しました。",
                ex,
                "アプリケーションを再起動し、問題が続く場合はログを確認してください。"
            );
        }
    }

    /// <summary>
    /// セグメント処理を実行します
    /// </summary>
    private async Task<List<TextSegment>> ProcessSegmentsAsync(VoiceRequest request, bool noCache, CancellationToken cancellationToken)
    {
        try
        {
            return await this._segmentProcessor.ProcessSegmentsAsync(request, noCache, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            this._logger.LogInformation("セグメント処理がキャンセルされました");
            throw new VoicevoxRunCachedException(
                ErrorCodes.General.OPERATION_CANCELLED,
                "Segment processing was cancelled",
                "セグメント処理がキャンセルされました。",
                null,
                "操作を再実行してください。"
            );
        }
        catch (VoicevoxRunCachedException)
        {
            // 既に適切に処理された例外は再スロー
            throw;
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "セグメント処理中に予期しないエラーが発生しました");
            throw new VoicevoxRunCachedException(
                ErrorCodes.General.UNKNOWN_ERROR,
                $"Unexpected error during segment processing: {ex.Message}",
                "セグメント処理中に予期しないエラーが発生しました。",
                ex,
                "アプリケーションを再起動し、問題が続く場合はログを確認してください。"
            );
        }
    }
}
