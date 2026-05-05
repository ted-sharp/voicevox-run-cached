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
/// 合成の詳細は <see cref="SegmentProducer"/>、再生は <see cref="AudioPlayer"/> に委譲する。本クラスは orchestration に専念する。
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
    private readonly SegmentProducer _producer;

    public TextToSpeechProcessor(
        VoiceVoxEngineManager engineManager,
        TextSegmentProcessor textProcessor,
        AudioExportService audioExportService,
        AudioPlayer audioPlayer,
        AudioCacheManager cacheManager,
        VoiceVoxApiClient apiClient,
        FillerManager fillerManager,
        SegmentProducer producer,
        ILogger logger)
    {
        _engineManager = engineManager ?? throw new ArgumentNullException(nameof(engineManager));
        _textProcessor = textProcessor ?? throw new ArgumentNullException(nameof(textProcessor));
        _audioExportService = audioExportService ?? throw new ArgumentNullException(nameof(audioExportService));
        _audioPlayer = audioPlayer ?? throw new ArgumentNullException(nameof(audioPlayer));
        _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _fillerManager = fillerManager ?? throw new ArgumentNullException(nameof(fillerManager));
        _producer = producer ?? throw new ArgumentNullException(nameof(producer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<int> ProcessTextToSpeechAsync(VoiceRequest request, TextToSpeechOptions options, CancellationToken cancellationToken = default)
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

        var segments = await LoadSegmentsAsync(request, options.NoCache, cancellationToken);
        if (segments.Count == 0)
        {
            throw new VoicevoxRunCachedException(
                ErrorCodes.General.InvalidArguments,
                "No segments to process",
                "処理対象のセグメントがありません。",
                null,
                "テキストが空でないか確認してください。");
        }

        if (options.CacheOnly)
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

        var synthesisResult = await SynthesizeAndPlayAsync(segments, request, options.NoCache, options.NoPlay, options.Verbose, cancellationToken);

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
                $"Warning: {synthesisResult.FailureCount}/{synthesisResult.AttemptedCount} segments failed to synthesize");
        }

        if (!String.IsNullOrWhiteSpace(options.OutPath))
        {
            await _audioExportService.ExportSegmentsAsync(segments, options.OutPath, cancellationToken);
        }

        if (options.NoPlay)
        {
            ConsoleHelper.WriteSuccess("Done (no-play mode)!");
        }

        LogElapsedIfVerbose(options.Verbose, totalStartTime);
        return hasPartialFailure ? 2 : 0;
    }

    private async Task<List<TextSegment>> LoadSegmentsAsync(VoiceRequest request, bool noCache, CancellationToken cancellationToken)
    {
        ConsoleHelper.WriteLine("Processing segments...");

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

        var producerTask = _producer.ProduceAsync(segments, channel.Writer, request, noCache, sharedCts.Token);
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
            ConsoleHelper.WriteLine($"Audio playback completed in {elapsed:F1}ms");
        }
    }

    private static void LogElapsedIfVerbose(bool verbose, DateTime startedAt)
    {
        if (!verbose)
        {
            return;
        }
        ConsoleHelper.WriteLine($"Total execution time: {(DateTime.UtcNow - startedAt).TotalMilliseconds:F1}ms");
    }
}
