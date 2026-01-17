using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services.Commands;

/// <summary>
/// テキスト読み上げのメイン処理フロー制御を行うクラス
/// </summary>
public class TextToSpeechProcessor : IDisposable
{
    private readonly AudioExportService _audioExportService;
    private readonly AudioPlayer _audioPlayer;
    private readonly EngineCoordinator _engineCoordinator;
    private readonly ILogger _logger;
    private readonly SegmentProcessor _segmentProcessor;
    private readonly AppSettings _settings;
    private bool _disposed;

    public TextToSpeechProcessor(AppSettings settings, ILogger logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _engineCoordinator = new EngineCoordinator(settings.VoiceVox, logger);
        _audioExportService = new AudioExportService(settings, logger);
        _segmentProcessor = new SegmentProcessor(settings, logger);
        _audioPlayer = new AudioPlayer(settings.Audio);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                try
                {
                    _audioPlayer?.Dispose();
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "TextToSpeechProcessorの破棄中にエラーが発生しました");
                }
            }
            _disposed = true;
        }
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
            await SynthesizeMissingSegmentsAsync(segments, request, noCache, cancellationToken);

            // --no-playが指定されている場合、再生をスキップ
            if (noPlay)
            {
                // ファイル出力（--out指定時）
                if (!String.IsNullOrWhiteSpace(outPath))
                {
                    await _audioExportService.ExportSegmentsAsync(segments, outPath, cancellationToken);
                }

                ConsoleHelper.WriteSuccess("Done (no-play mode)!", _logger);
                if (verbose)
                {
                    ConsoleHelper.WriteLine($"Total execution time: {(DateTime.UtcNow - totalStartTime).TotalMilliseconds:F1}ms", _logger);
                }
                return 0;
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


    /// <summary>
    /// 音声再生処理を実行します（フィラー機能付き）
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
            var cacheManager = new AudioCacheManager(_settings.Cache);
            using var processingChannel = new AudioProcessingChannel(cacheManager, new VoiceVoxApiClient(_settings.VoiceVox));
            var fillerManager = new FillerManager(_settings.Filler, _settings.VoiceVox.DefaultSpeaker);

            await _audioPlayer.PlayAudioSequentiallyWithGenerationAsync(
                segments, processingChannel, fillerManager, cancellationToken);

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

    private async Task SynthesizeMissingSegmentsAsync(List<TextSegment> segments, VoiceRequest originalRequest, bool noCache, CancellationToken cancellationToken)
    {
        var missingSegments = segments.Where(s => s.AudioData == null).ToList();
        if (missingSegments.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Synthesizing {Count} missing segments...", missingSegments.Count);

        using var client = new VoiceVoxApiClient(_settings.VoiceVox);
        using var cacheManager = new AudioCacheManager(_settings.Cache);

        await client.InitializeSpeakerAsync(originalRequest.SpeakerId, cancellationToken);

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

                // 音声合成
                var audioQuery = await client.GenerateAudioQueryAsync(segmentRequest, cancellationToken);
                var wavData = await client.SynthesizeAudioAsync(audioQuery, originalRequest.SpeakerId, cancellationToken);

                segment.AudioData = wavData;

                // キャッシュに保存（noCacheフラグがfalseの場合のみ）
                if (!noCache)
                {
                    await cacheManager.SaveAudioCacheAsync(segmentRequest, wavData);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to synthesize segment: {Text}", segment.Text);
            }
        }
    }
}
