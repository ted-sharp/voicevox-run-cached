using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
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
    private readonly IClock _clock;

    public TextToSpeechProcessor(
        SpeechEngineProvider engines,
        TextSegmentProcessor textProcessor,
        AudioExportService audioExportService,
        AudioPlayer audioPlayer,
        AudioCacheManager cacheManager,
        FillerManager fillerManager,
        SegmentProducer producer,
        SpeechLock speechLock,
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
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <remarks>
    /// 処理全体 (キャッシュ参照 → 合成 → 再生 → <c>--out</c>) を <see cref="SpeechLock"/> で
    /// プロセス間に直列化する。Hooks が同時に発火しても音声が重ならず、合成エンジンの直列実行と
    /// エンジンの起動もプロセスをまたいで 1 つずつになる。後から来た方は先の読み上げが終わるまで待つ
    /// (上限は <c>--max-wait</c> / <c>Lock.MaxWaitSeconds</c>)。
    /// </remarks>
    public async Task<int> ProcessTextToSpeechAsync(VoiceRequest request, TextToSpeechOptions options, CancellationToken cancellationToken = default)
    {
        if (!String.IsNullOrWhiteSpace(options.OutPath))
        {
            AudioExportService.EnsureWritableOutput(options.OutPath);
        }
        return await _speechLock.RunAsync(() => ProcessLockedAsync(request, options, cancellationToken), cancellationToken, options.MaxWaitSeconds);
    }

    private async Task<int> ProcessLockedAsync(VoiceRequest request, TextToSpeechOptions options, CancellationToken cancellationToken)
    {
        var totalStartTime = _clock.UtcNow;

        if (!options.NoPlay)
        {
            // デバイスの warm-up (PrepareDevice) はキャッシュ参照・エンジン起動と並行させる
            _audioPlayer.BeginDevicePreparation();
        }

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

        var synthesisResult = await SynthesizeAndPlayAsync(segments, request, options, cancellationToken);

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
    /// Producer (合成) と Consumer (再生) を <see cref="Channel{T}"/> で繋ぎ、両方が並走しながら処理する。
    /// </summary>
    private async Task<SynthesisResult> SynthesizeAndPlayAsync(
        List<TextSegment> segments,
        VoiceRequest request,
        TextToSpeechOptions options,
        CancellationToken cancellationToken)
    {
        // エンジンの準備は producer が最初に合成が必要になったときに 1 回だけ呼ぶ。全部キャッシュから読めればエンジンに一切接続しない
        // (起動待ちで通知が遅れない・エンジン停止中でも鳴る)。先頭がキャッシュ済みなら、エンジンの起動を待つ間にそこから鳴り始める (その間フィラーも鳴りうる)。
        // 準備の失敗は producer が投げずに返す (投げると鳴っているキャッシュ済みの文が途中で止められる)。--cache-only は合成しない (読めないキャッシュは失敗として数える)
        // 準備の中身 (起動確認・話者の確認と初期化) は ISpeechEngine.PrepareAsync
        Func<CancellationToken, Task>? prepareSynthesis = options.CacheOnly
            ? null
            : ct => _engines.Get(request.Engine).PrepareAsync(request.Speaker, ct);

        var channel = Channel.CreateUnbounded<TextSegment>(new UnboundedChannelOptions
        {
            SingleWriter = true,
            SingleReader = true,
        });

        // 一方が落ちたらもう一方も止めるための Linked CTS
        using var sharedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var producerTask = _producer.ProduceAsync(
            segments, channel.Writer, request, saveToCache: !options.NoCache, prepareSynthesis, sharedCts.Token);
        var playbackTask = options.NoPlay
            ? Task.CompletedTask
            : RunPlaybackAsync(channel.Reader, request, options.Verbose, sharedCts.Token);

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

    private async Task RunPlaybackAsync(ChannelReader<TextSegment> reader, VoiceRequest request, bool verbose, CancellationToken cancellationToken)
    {
        var playbackStartTime = _clock.UtcNow;
        // フィラーも読み上げと同じ話者の声 (--init -s で話者ごとに生成したもの)・同じ音量にする。文と待ち 1 回の回数はエンジンごと
        var engine = _engines.Get(request.Engine);
        await _audioPlayer.PlayStreamingAsync(
            reader,
            ct => _fillerManager.GetRandomFillerAudioAsync(engine, request.Speaker, request.Volume, ct),
            engine.FillerSettings.MaxFillersPerWait,
            cancellationToken);
        _fillerManager.MarkUsed(engine, request.Speaker);

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
