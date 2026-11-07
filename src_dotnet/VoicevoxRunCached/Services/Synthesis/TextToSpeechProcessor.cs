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
    private readonly IClock _clock;

    public TextToSpeechProcessor(
        VoiceVoxEngineManager engineManager,
        TextSegmentProcessor textProcessor,
        AudioExportService audioExportService,
        AudioPlayer audioPlayer,
        AudioCacheManager cacheManager,
        VoiceVoxApiClient apiClient,
        FillerManager fillerManager,
        SegmentProducer producer,
        IClock clock,
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
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <remarks>
    /// 処理全体 (キャッシュ参照 → 合成 → 再生 → <c>--out</c>) を <see cref="InterProcessLock.SpeechLockPath"/> で
    /// プロセス間に直列化する。Hooks が同時に発火しても音声が重ならず、VOICEVOX API の直列実行と
    /// エンジンの起動もプロセスをまたいで 1 つずつになる。後から来た方は先の読み上げが終わるまで待つ。
    /// </remarks>
    public async Task<int> ProcessTextToSpeechAsync(VoiceRequest request, TextToSpeechOptions options, CancellationToken cancellationToken = default)
    {
        using var speechLock = await InterProcessLock.AcquireAsync(InterProcessLock.SpeechLockPath, _logger, cancellationToken);

        var totalStartTime = _clock.UtcNow;

        if (!options.NoPlay)
        {
            // デバイスの warm-up (PrepareDevice) はキャッシュ参照・エンジン起動と並行させる
            _audioPlayer.BeginDevicePreparation();
        }

        // エンジンの確認・起動はここではしない。全セグメントがキャッシュ済み (--cache-only を含む) なら
        // エンジンは不要なので、合成が必要なときだけ SynthesizeAndPlayAsync で行う。
        // --refresh はキャッシュを読まずに全部合成し直し、保存時に上書きする (--no-cache は読みも書きもしない)
        var segments = await LoadSegmentsAsync(request, skipLookup: options.NoCache || options.Refresh, cancellationToken);
        if (segments.Count == 0)
        {
            throw new VoicevoxRunCachedException(
                errorCode: ErrorCodes.General.InvalidArguments,
                message: "No segments to process",
                userMessage: "処理対象のセグメントがありません。",
                suggestedSolution: "テキストが空でないか確認してください。");
        }

        if (options.CacheOnly)
        {
            var uncachedCount = segments.Count(s => !s.IsCached);
            if (uncachedCount > 0)
            {
                throw new VoicevoxRunCachedException(
                    errorCode: ErrorCodes.Cache.CacheReadError,
                    message: $"{uncachedCount} segments are not cached",
                    userMessage: $"{uncachedCount} 個のセグメントがキャッシュに存在しません。",
                    suggestedSolution: "--cache-only を外すか、事前に該当テキストを合成してキャッシュを作成してください。");
            }
        }

        var synthesisResult = await SynthesizeAndPlayAsync(segments, request, options, cancellationToken);

        if (synthesisResult.AttemptedCount > 0 && synthesisResult.SuccessCount == 0)
        {
            throw new VoicevoxRunCachedException(
                errorCode: ErrorCodes.Audio.AudioGenerationFailed,
                message: $"All {synthesisResult.AttemptedCount} segments failed to synthesize",
                userMessage: "全てのセグメントの音声合成に失敗しました。",
                suggestedSolution: "VOICEVOXエンジンの状態とログを確認してください。");
        }

        bool hasPartialFailure = synthesisResult.FailureCount > 0;
        if (hasPartialFailure)
        {
            _logger.LogWarning(
                "セグメント合成が部分的に失敗しました: {FailureCount}/{AttemptedCount}",
                synthesisResult.FailureCount, synthesisResult.AttemptedCount);
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
        return hasPartialFailure ? ErrorCodes.PartialFailureExitCode : 0;
    }

    private async Task<List<TextSegment>> LoadSegmentsAsync(VoiceRequest request, bool skipLookup, CancellationToken cancellationToken)
    {
        ConsoleHelper.WriteLine("Processing segments...");

        var segments = await _textProcessor.ProcessTextAsync(request.Text, request.SpeakerId, cancellationToken);
        if (segments.Count == 0)
        {
            _logger.LogWarning("処理可能なセグメントがありません");
            return segments;
        }

        _logger.LogInformation("テキストを {SegmentCount} 個のセグメントに分割しました", segments.Count);

        if (skipLookup)
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
        TextToSpeechOptions options,
        CancellationToken cancellationToken)
    {
        var anyMissing = segments.Any(s => !s.IsCached);
        if (anyMissing)
        {
            // 合成が必要なときだけエンジンを確認する (停止中で AutoStartEngine なら起動を待つ)。
            // 全部キャッシュ済みならエンジンに一切接続しない (起動待ちで通知が遅れない・エンジン停止中でも鳴る)
            await _engineManager.EnsureRunningOrThrowAsync(cancellationToken);

            // 存在しない speaker だとエンジンは /initialize_speaker で 500 を返し、リトライ込みで十数秒待たされる。
            // /speakers の照会はローカルで数 ms なので、合成が必要なときだけ先に確認して即エラーにする。
            EnsureSpeakerExists(await _apiClient.GetSpeakersAsync(cancellationToken), request.SpeakerId);
            await _apiClient.InitializeSpeakerAsync(request.SpeakerId, cancellationToken);
        }

        var channel = Channel.CreateUnbounded<TextSegment>(new UnboundedChannelOptions
        {
            SingleWriter = true,
            SingleReader = true,
        });

        // 一方が落ちたらもう一方も止めるための Linked CTS
        using var sharedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var producerTask = _producer.ProduceAsync(segments, channel.Writer, request, saveToCache: !options.NoCache, sharedCts.Token);
        var playbackTask = options.NoPlay
            ? Task.CompletedTask
            : RunPlaybackAsync(channel.Reader, options.Verbose, sharedCts.Token);

        await WhenAllOrCancelOnFaultAsync(producerTask, playbackTask, sharedCts);
        return await producerTask;
    }

    /// <summary>
    /// 2 つの Task の完了を待つ。先に終わった側が失敗していたら、その時点で <paramref name="cts"/> をキャンセルして
    /// もう一方を止め、その終了を待ってから最初の例外を投げる。
    /// </summary>
    /// <remarks>
    /// <see cref="Task.WhenAll(Task[])"/> だけだと両方が終わるまで戻らないため、再生がデバイスエラーで落ちても
    /// producer が残りの全セグメントを合成し終えるまでエラーにならない。
    /// </remarks>
    public static async Task WhenAllOrCancelOnFaultAsync(Task first, Task second, CancellationTokenSource cts)
    {
        ArgumentNullException.ThrowIfNull(cts);

        try
        {
            var finished = await Task.WhenAny(first, second);
            await finished; // 先に終わった側が失敗していればここで例外になる
            await Task.WhenAll(first, second);
        }
        catch
        {
            cts.Cancel();
            try
            { await Task.WhenAll(first, second); }
            catch
            { /* キャンセルで起きる二次例外は、先に起きた例外を上書きしないよう握る */ }
            throw;
        }
    }

    /// <summary>
    /// speaker ID (= スタイル ID) がエンジンの話者一覧に存在しなければ <see cref="ErrorCodes.General.InvalidArguments"/> で失敗させる。
    /// </summary>
    public static void EnsureSpeakerExists(IEnumerable<Speaker> speakers, int speakerId)
    {
        if (speakers.Any(s => s.Styles.Any(style => style.Id == speakerId)))
        {
            return;
        }

        throw new VoicevoxRunCachedException(
            errorCode: ErrorCodes.General.InvalidArguments,
            message: $"Speaker {speakerId} does not exist in the engine",
            userMessage: $"speaker ID {speakerId} はエンジンに存在しません。",
            suggestedSolution: "`VoicevoxRunCached speakers` で利用可能な ID を確認してください。");
    }

    private async Task RunPlaybackAsync(ChannelReader<TextSegment> reader, bool verbose, CancellationToken cancellationToken)
    {
        var playbackStartTime = _clock.UtcNow;
        await _audioPlayer.PlayStreamingAsync(reader, _fillerManager, cancellationToken);

        if (verbose)
        {
            var elapsed = (_clock.UtcNow - playbackStartTime).TotalMilliseconds;
            ConsoleHelper.WriteLine($"Audio playback completed in {elapsed:F1}ms");
        }
    }

    private void LogElapsedIfVerbose(bool verbose, DateTime startedAt)
    {
        if (!verbose)
        {
            return;
        }
        ConsoleHelper.WriteLine($"Total execution time: {(_clock.UtcNow - startedAt).TotalMilliseconds:F1}ms");
    }
}
