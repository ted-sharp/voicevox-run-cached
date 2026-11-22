using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services.Synthesis;

/// <summary>
/// テキスト読み上げのメイン処理フロー制御。
/// セグメント分割 → キャッシュ参照 → デコード・不足分の合成と再生を <see cref="Channel{T}"/> 経由で並走 → ファイル出力 までを担う。
/// </summary>
/// <remarks>
/// 合成の詳細は <see cref="SegmentProducer"/>、再生は <see cref="AudioPlayer"/> に委譲する。本クラスは orchestration に専念する。
/// </remarks>
public class TextToSpeechProcessor
{
    private readonly AudioCacheManager _cacheManager;
    private readonly AudioExportService _audioExportService;
    private readonly AudioPlayer _audioPlayer;
    private readonly SpeechEngineProvider _engines;
    private readonly FillerManager _fillerManager;
    private readonly ILogger _logger;
    private readonly TextSegmentProcessor _textProcessor;
    private readonly SegmentProducer _producer;
    private readonly SpeechLock _speechLock;
    private readonly PlaybackLock _playbackLock;
    private readonly IClock _clock;
    private readonly AudioSettings _audioSettings;

    public TextToSpeechProcessor(
        SpeechEngineProvider engines,
        TextSegmentProcessor textProcessor,
        AudioExportService audioExportService,
        AudioPlayer audioPlayer,
        AudioCacheManager cacheManager,
        FillerManager fillerManager,
        SegmentProducer producer,
        SpeechLock speechLock,
        PlaybackLock playbackLock,
        AudioSettings audioSettings,
        IClock clock,
        ILogger logger)
    {
        _engines = engines ?? throw new ArgumentNullException(nameof(engines));
        _textProcessor = textProcessor ?? throw new ArgumentNullException(nameof(textProcessor));
        _audioExportService = audioExportService ?? throw new ArgumentNullException(nameof(audioExportService));
        _audioPlayer = audioPlayer ?? throw new ArgumentNullException(nameof(audioPlayer));
        _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
        _fillerManager = fillerManager ?? throw new ArgumentNullException(nameof(fillerManager));
        _producer = producer ?? throw new ArgumentNullException(nameof(producer));
        _speechLock = speechLock ?? throw new ArgumentNullException(nameof(speechLock));
        _playbackLock = playbackLock ?? throw new ArgumentNullException(nameof(playbackLock));
        _audioSettings = audioSettings ?? throw new ArgumentNullException(nameof(audioSettings));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <remarks>
    /// <para>キャッシュ参照と合成は <see cref="SpeechLock"/> の中で行い、プロセス間に直列化する
    /// (合成エンジンの呼び出しと起動・キャッシュの読み書きがプロセスをまたいで 1 つずつになる)。
    /// 再生は合成と並走させ、再生デバイスごとの <see cref="PlaybackLock"/> で直列化する。
    /// 合成を終えたら、再生の途中でも <see cref="SpeechLock"/> を手放す。同じデバイスの読み上げは前の再生が終わるまで待ち (音声が重ならない)、
    /// 違うデバイスの読み上げはその間に合成・再生できる。</para>
    /// <para>待つ上限 (<c>--max-wait</c> / <c>Lock.MaxWaitSeconds</c>) は、2 つのロックを待った時間の合計に掛ける。</para>
    /// <para>ロックを手放した後の再生と <c>--out</c> は、メモリ上の音声だけを使い、エンジン・キャッシュに触れない。</para>
    /// </remarks>
    public async Task<int> ProcessTextToSpeechAsync(VoiceRequest request, TextToSpeechOptions options, CancellationToken cancellationToken = default)
    {
        if (!String.IsNullOrWhiteSpace(options.OutPath))
        {
            AudioExportService.EnsureWritableOutput(options.OutPath);
        }

        var device = OutputDeviceSelector.Resolve(_audioSettings, request, options.Device);
        var maxWait = _speechLock.ResolveMaxWait(options.MaxWaitSeconds);
        var requestedAt = _clock.UtcNow;

        // 再生は SpeechLock を手放した後も続くので、producer と再生を一緒に止める CTS はロックの外で持つ
        using var sharedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var run = await _speechLock.RunAsync(
            () =>
            {
                var playback = new PlaybackTarget(device, maxWait, AlreadyWaited: _clock.UtcNow - requestedAt);
                return SynthesizeLockedAsync(request, options, playback, sharedCts, cancellationToken);
            },
            cancellationToken,
            options.MaxWaitSeconds);

        // producer は終わっているので、ここで失敗しても止めるものは無い
        await run.Playback;
        return await CompleteAsync(run, options, cancellationToken);
    }

    /// <summary>
    /// 再生するデバイスと、<see cref="PlaybackLock"/> を待つ上限 (<see cref="SpeechLock"/> を待った時間を引いて使う)。
    /// </summary>
    private sealed record PlaybackTarget(int Device, TimeSpan? MaxWait, TimeSpan AlreadyWaited);

    /// <summary>
    /// <see cref="SpeechLock"/> の中での処理の結果。<see cref="Playback"/> はまだ終わっていないことがある。
    /// </summary>
    private sealed record SynthesisRun(List<TextSegment> Segments, SynthesisResult Result, Task Playback, DateTime StartedAt);

    /// <summary>
    /// キャッシュ参照と合成を行い、再生を始める。合成が終わったら (再生の途中でも) 戻る。
    /// </summary>
    private async Task<SynthesisRun> SynthesizeLockedAsync(
        VoiceRequest request,
        TextToSpeechOptions options,
        PlaybackTarget playback,
        CancellationTokenSource sharedCts,
        CancellationToken cancellationToken)
    {
        var totalStartTime = _clock.UtcNow;

        if (!options.NoPlay)
        {
            // デバイスの warm-up (PrepareDevice) はキャッシュ参照・エンジン起動と並行させる
            // (PlaybackLock を取る前なので、同じデバイスの先の再生に小さな音量で重なることがある)
            _audioPlayer.BeginDevicePreparation(playback.Device);
        }

        // この話者のフィラーを --init が使っていないものとして消さないようにする
        _fillerManager.MarkUsed(_engines.Get(request.Engine), request.Speaker);

        // エンジンの確認・起動はここではしない。合成が必要になったときだけ SegmentProducer が行う。
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

        var (result, playbackTask) = await SynthesizeAndStartPlaybackAsync(segments, request, options, playback, sharedCts);
        return new SynthesisRun(segments, result, playbackTask, totalStartTime);
    }

    /// <summary>
    /// 再生が終わった後の判定と <c>--out</c> の保存。
    /// </summary>
    private async Task<int> CompleteAsync(SynthesisRun run, TextToSpeechOptions options, CancellationToken cancellationToken)
    {
        var (segments, synthesisResult, _, totalStartTime) = run;

        // 準備の失敗 (エンジン無し・話者無し) は、キャッシュ済みの文を鳴らし終えてから投げる (終了コード 3 / 1)。
        // 一部失敗 (8) にはしない (原因がエンジンの設定・入力にあり、文ごとの失敗ではないため)。--out も保存しない
        if (synthesisResult.PreparationError is { } preparationError)
        {
            ExceptionDispatchInfo.Throw(preparationError);
        }

        // キャッシュから鳴ったセグメントは SynthesisResult に数えないので、失敗がセグメント数に達したときだけ「全部失敗」にする
        // (合成した文が全部失敗しても、キャッシュから鳴った文があれば一部失敗)
        if (synthesisResult.FailureCount == segments.Count)
        {
            throw synthesisResult.ToAllFailedException(
                $"All {synthesisResult.AttemptedCount} segments failed to synthesize", "全てのセグメントの音声合成に失敗しました。");
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

        var segments = await _textProcessor.ProcessTextAsync(request.Text, cancellationToken);
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
    /// Producer (合成) と Consumer (再生) を <see cref="Channel{T}"/> で繋いで並走させ、producer が終わったら戻る。
    /// 返す再生の Task はまだ終わっていないことがある (呼び出し元が <see cref="SpeechLock"/> を手放してから待つ)。
    /// </summary>
    /// <remarks>
    /// producer が終わる前に再生が失敗したら producer も止めて例外を投げる (<see cref="WhenFirstCompletesOrCancelOnFaultAsync"/>)。
    /// </remarks>
    private async Task<(SynthesisResult Result, Task Playback)> SynthesizeAndStartPlaybackAsync(
        List<TextSegment> segments,
        VoiceRequest request,
        TextToSpeechOptions options,
        PlaybackTarget playback,
        CancellationTokenSource sharedCts)
    {
        // エンジンの準備 (ISpeechEngine.PrepareAsync) は producer が最初に合成が必要になったときに 1 回だけ呼ぶ (理由は SegmentProducer)。
        // --cache-only は合成しない
        Func<CancellationToken, Task>? prepareSynthesis = options.CacheOnly
            ? null
            : ct => _engines.Get(request.Engine).PrepareAsync(request.Speaker, ct);

        var channel = Channel.CreateUnbounded<TextSegment>(new UnboundedChannelOptions
        {
            SingleWriter = true,
            SingleReader = true,
        });

        var producerTask = _producer.ProduceAsync(
            segments, channel.Writer, request, saveToCache: !options.NoCache, prepareSynthesis, sharedCts.Token);
        var playbackTask = options.NoPlay
            ? Task.CompletedTask
            : RunPlaybackAsync(channel.Reader, request, playback, options.Verbose, sharedCts.Token);

        await WhenFirstCompletesOrCancelOnFaultAsync(producerTask, playbackTask, sharedCts);
        return (await producerTask, playbackTask);
    }

    /// <summary>
    /// <paramref name="first"/> の完了を待つ。その前にどちらかが失敗したら、その時点で <paramref name="cts"/> をキャンセルして
    /// もう一方を止め、両方の終了を待ってから最初の例外を投げる。<paramref name="second"/> は終わっていなくても戻る。
    /// </summary>
    /// <remarks>
    /// producer (<paramref name="first"/>) が終われば <see cref="SpeechLock"/> を手放せるが、再生 (<paramref name="second"/>) はまだ続くため。
    /// <see cref="Task.WhenAny(Task[])"/> で先に終わった側を見るのは、再生がデバイスエラーで落ちたとき、producer が残りの全セグメントを
    /// 合成し終えるのを待たずにエラーにするため。
    /// </remarks>
    public static async Task WhenFirstCompletesOrCancelOnFaultAsync(Task first, Task second, CancellationTokenSource cts)
    {
        ArgumentNullException.ThrowIfNull(cts);

        try
        {
            var finished = await Task.WhenAny(first, second);
            await finished; // 先に終わった側が失敗していればここで例外になる
            await first;
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

    /// <remarks>
    /// <see cref="PlaybackLock"/> は consumer の中で待つ。待っている間も producer は合成を進めるので、前の再生が終わればすぐ鳴らせる。
    /// </remarks>
    private async Task RunPlaybackAsync(ChannelReader<TextSegment> reader, VoiceRequest request, PlaybackTarget target, bool verbose, CancellationToken cancellationToken)
    {
        using var playbackLock = await _playbackLock.AcquireAsync(target.Device, target.MaxWait, target.AlreadyWaited, cancellationToken);
        var playbackStartTime = _clock.UtcNow;
        // フィラーも読み上げと同じ話者の声 (--init -s で話者ごとに生成したもの)・同じ音量にする。文と待ち 1 回の回数はエンジンごと
        var engine = _engines.Get(request.Engine);
        await _audioPlayer.PlayStreamingAsync(
            reader,
            target.Device,
            ct => _fillerManager.GetRandomFillerAudioAsync(engine, request.Speaker, request.Volume, ct),
            engine.FillerSettings.MaxFillersPerWait,
            cancellationToken);

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
