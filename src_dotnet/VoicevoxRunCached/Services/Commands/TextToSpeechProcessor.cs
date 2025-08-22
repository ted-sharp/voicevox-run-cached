using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services;

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
            if (!string.IsNullOrWhiteSpace(outPath))
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

            // キャッシュのみモードの場合、キャッシュされていないセグメントがあるかチェック
            if (cacheOnly)
            {
                var uncachedSegments = segments.Where(s => !s.IsCached).ToList();
                if (uncachedSegments.Count > 0)
                {
                    ConsoleHelper.WriteWarning($"Warning: {uncachedSegments.Count} segments are not cached", this._logger);
                    return 1;
                }
            }

            // 音声再生処理
            var playbackResult = await this.ProcessAudioPlaybackAsync(segments, verbose, cancellationToken);
            if (playbackResult != 0)
            {
                return playbackResult;
            }

            // 出力ファイルの処理完了を待機
            if (exportTask != null)
            {
                await exportTask;
            }

            if (verbose)
            {
                var totalElapsed = (DateTime.UtcNow - totalStartTime).TotalMilliseconds;
                ConsoleHelper.WriteLine($"Total execution time: {totalElapsed:F1}ms", this._logger);
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            ConsoleHelper.WriteLine("Text-to-speech processing was cancelled", this._logger);
            return 1;
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "テキスト読み上げ処理中にエラーが発生しました");
            ConsoleHelper.WriteError($"Error: {ex.Message}", this._logger);
            return 1;
        }
    }

    /// <summary>
    /// 音声再生処理を実行します
    /// </summary>
    /// <param name="segments">セグメントのリスト</param>
    /// <param name="verbose">詳細出力フラグ</param>
    /// <param name="cancellationToken">キャンセレーショントークン</param>
    /// <returns>処理結果の終了コード</returns>
    private async Task<int> ProcessAudioPlaybackAsync(List<TextSegment> segments, bool verbose, CancellationToken cancellationToken)
    {
        try
        {
            var playbackStartTime = DateTime.UtcNow;

            // 音声再生処理
            var audioSegmentPlayer = new AudioSegmentPlayer(this._settings.Audio, new AudioFormatDetector());
            var cacheManager = new AudioCacheManager(this._settings.Cache);
            using var processingChannel = new AudioProcessingChannel(cacheManager, new VoiceVoxApiClient(this._settings.VoiceVox));
            var fillerManager = new FillerManager(this._settings.Filler, cacheManager, this._settings.VoiceVox.DefaultSpeaker);

            await audioSegmentPlayer.PlayAudioSequentiallyWithGenerationAsync(
                segments, processingChannel, fillerManager, cancellationToken);

            var playbackElapsed = (DateTime.UtcNow - playbackStartTime).TotalMilliseconds;
            if (verbose)
            {
                ConsoleHelper.WriteLine($"Audio playback completed in {playbackElapsed:F1}ms", this._logger);
            }

            return 0;
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "音声再生処理中にエラーが発生しました");
            ConsoleHelper.WriteError($"Error: Failed to play audio: {ex.Message}", this._logger);
            return 1;
        }
    }
}
