using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoicevoxRunCached.Constants;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services.Audio;

/// <summary>
/// 音声再生の統合制御。Channel 経由で渡されるセグメントを順次再生し、合成待ち中はフィラー音声で隙間を埋める。
/// </summary>
/// <remarks>
/// 1 回の読み上げのあいだ、共有 <c>WavePlayer</c> を 1 回だけ開いて <see cref="PlaybackStream"/> を流し続ける
/// (文ごとに止めると Bluetooth のデバイスが休み、次の文の始めが切れる。理由は <see cref="PlaybackStream"/>)。
/// </remarks>
public class AudioPlayer : IDisposable
{
    private readonly AudioDeviceManager _deviceManager;
    private readonly AudioFormatDetector _formatDetector;
    private readonly WavePlayerManager _wavePlayerManager;
    private readonly ILogger _logger;
    private bool _disposed;

    public AudioPlayer(
        AudioDeviceManager deviceManager,
        AudioFormatDetector formatDetector,
        WavePlayerManager wavePlayerManager,
        ILogger logger)
    {
        _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
        _formatDetector = formatDetector ?? throw new ArgumentNullException(nameof(formatDetector));
        _wavePlayerManager = wavePlayerManager ?? throw new ArgumentNullException(nameof(wavePlayerManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        // StopAudio 自身が try/catch を持つため二重防御は不要
        StopAudio();
        _disposed = true;
    }

    /// <summary>
    /// 再生デバイスの warm-up (<c>Audio.PrepareDevice</c>) をバックグラウンドで始める。
    /// 再生することが決まった時点で呼ぶと、合成待ちなどと並行して温められる。呼ばなくても再生開始時に始まる。
    /// </summary>
    /// <param name="device">WaveOut の index (-1 は OS 既定)</param>
    public void BeginDevicePreparation(int device) => _deviceManager.StartPreparation(device);

    /// <summary>
    /// セグメントが届くたびに順次再生する。次のセグメントが <see cref="AudioConstants.FillerStartDelayMs"/>
    /// 以内に届かない場合はフィラー音声で隙間を埋める。
    /// </summary>
    /// <param name="reader">再生するセグメントの channel</param>
    /// <param name="device">再生デバイス (WaveOut の index。-1 は OS 既定)</param>
    /// <param name="getFillerAudio">フィラー音声 (Ogg Opus か WAV。<see cref="AudioFormatDetector.CreateWaveStream"/> で読む) を返す。無効・未生成なら null を返す。null ならフィラーを挟まない</param>
    /// <param name="maxFillersPerWait">
    /// 次のセグメントを待つ 1 回のあいだに鳴らすフィラーの上限 (エンジンの <c>MaxFillersPerWait</c>)。null なら上限なし、0 なら鳴らさない
    /// </param>
    /// <param name="leadingSilence">最初の音 (本編かフィラー) の前に足す無音 (<see cref="Configuration.DeviceOptionSettings.LeadingSilenceMs"/>)</param>
    /// <param name="cancellationToken">キャンセル</param>
    /// <remarks>
    /// 呼び出し元 (producer) が <see cref="ChannelWriter{T}.Complete"/> するまでループする。
    /// AudioData が null のセグメントは合成失敗扱いで読み飛ばす。
    /// </remarks>
    public async Task PlayStreamingAsync(
        ChannelReader<TextSegment> reader,
        int device,
        Func<CancellationToken, Task<byte[]?>>? getFillerAudio = null,
        int? maxFillersPerWait = null,
        TimeSpan leadingSilence = default,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AudioPlayer));

        _logger.LogDebug("PlayStreamingAsync 開始 - デバイス: {Device}, フィラー: {HasFiller}, 待ち 1 回の上限: {MaxFillersPerWait}, 冒頭の無音: {LeadingSilenceMs}ms",
            device, getFillerAudio != null, maxFillersPerWait, leadingSilence.TotalMilliseconds);

        await _deviceManager.EnsureDeviceReadyAsync(device);

        var stream = new PlaybackStream();
        var wavePlayer = _wavePlayerManager.GetOrCreateSharedWavePlayer(device);

        // デバイスのエラーで止まったら、待っている音に伝える。例外の無い停止イベントは見ない
        // (前の読み上げで Stop() したときのイベントが、遅れてこの読み上げに届くことがあるため。止まったまま読まれなければタイムアウトになる)
        void OnPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception is { } error)
            {
                stream.Fail(error);
            }
        }

        wavePlayer.PlaybackStopped += OnPlaybackStopped;
        try
        {
            // 16bit にするのは、float を受け付けないドライバがあるため
            wavePlayer.Init(stream.ToWaveProvider16());
            wavePlayer.Play();

            bool isFirstPlayback = true;
            int playedCount = 0;
            // フィラーの取得・再生に失敗したら、この再生の間は null にしてフィラーを使わない
            var fillerSource = getFillerAudio;
            var fillerBudget = new FillerWaitBudget(maxFillersPerWait);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (reader.TryRead(out var segment))
                {
                    playedCount++;
                    fillerBudget.OnSegmentArrived();

                    if (segment.AudioData == null || segment.AudioData.Length == 0)
                    {
                        _logger.LogWarning("セグメント {Index} に音声データがありません。スキップします: {Text}",
                            playedCount, segment.Text);
                        continue;
                    }

                    _logger.LogDebug("セグメント {Index} を再生中 (size: {Size} bytes): \"{Text}\"",
                        playedCount, segment.AudioData.Length, segment.Text);

                    await PlayAsync(stream, segment.AudioData, isFirstPlayback, leadingSilence, cancellationToken);
                    isFirstPlayback = false;
                    continue;
                }

                // フィラーを使わない・この待ちで上限まで鳴らした: 次のセグメントが届くまでそのまま待つ
                if (fillerSource == null || !fillerBudget.CanPlay)
                {
                    if (!await reader.WaitToReadAsync(cancellationToken))
                    {
                        break;
                    }
                    continue;
                }

                // 次のセグメントが未到着 — フィラー閾値内に届かなければフィラーを挿入する
                var waitOutcome = await WaitForNextSegmentOrFillerTimeoutAsync(reader, cancellationToken);
                if (waitOutcome == NextSegmentWait.NoMore)
                {
                    break;
                }
                if (waitOutcome == NextSegmentWait.FillerTimeout)
                {
                    var filler = await TryPlayFillerAsync(stream, fillerSource, isFirstPlayback, leadingSilence, cancellationToken);
                    if (filler == FillerOutcome.Played)
                    {
                        isFirstPlayback = false;
                        fillerBudget.OnFillerPlayed();
                    }
                    else if (filler == FillerOutcome.Failed)
                    {
                        fillerSource = null;
                    }
                }
                // Ready の場合は次ループの TryRead で拾う
            }

            // 最後の音はまだ出力のバッファにあるので、その分と余白を無音で流してから止める
            await Task.Delay(_wavePlayerManager.OutputLatency + TimeSpan.FromMilliseconds(AudioConstants.TrailingSilenceMs), cancellationToken);
            _logger.LogDebug("ストリーミング再生が完了しました - 再生数: {Count}", playedCount);
        }
        finally
        {
            wavePlayer.PlaybackStopped -= OnPlaybackStopped;
            StopAudio();
            stream.Fail(new OperationCanceledException("再生を止めました"));
        }
    }

    public void StopAudio()
    {
        try
        {
            _wavePlayerManager.StopSharedWavePlayer();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "音声停止中にエラーが発生しました");
        }
    }

    private enum NextSegmentWait
    {
        /// <summary>次のセグメントが Channel に届いた</summary>
        Ready,
        /// <summary>FillerStartDelayMs を超過しても届かなかった</summary>
        FillerTimeout,
        /// <summary>writer が complete し、これ以上セグメントは来ない</summary>
        NoMore,
    }

    /// <summary>
    /// 次のセグメント到着を待つ。<see cref="AudioConstants.FillerStartDelayMs"/> を超えても届かなければ
    /// <see cref="NextSegmentWait.FillerTimeout"/> を返してフィラー挿入を促す。
    /// </summary>
    private static async Task<NextSegmentWait> WaitForNextSegmentOrFillerTimeoutAsync(
        ChannelReader<TextSegment> reader,
        CancellationToken cancellationToken)
    {
        var waitTask = reader.WaitToReadAsync(cancellationToken).AsTask();
        var fillerTask = Task.Delay(AudioConstants.FillerStartDelayMs, cancellationToken);
        var done = await Task.WhenAny(waitTask, fillerTask);

        if (done == fillerTask)
        {
            return NextSegmentWait.FillerTimeout;
        }
        return await waitTask ? NextSegmentWait.Ready : NextSegmentWait.NoMore;
    }

    private enum FillerOutcome
    {
        /// <summary>フィラーを再生した</summary>
        Played,
        /// <summary>フィラーが無効・未生成で、何もしなかった</summary>
        Skipped,
        /// <summary>取得・デコード・再生のどこかで失敗した</summary>
        Failed,
    }

    /// <summary>
    /// フィラー音声を再生する。
    /// </summary>
    /// <remarks>
    /// フィラーは無音を埋めるための飾りなので、失敗 (ファイルの読み込み・デコード・再生) で本編の読み上げごと止めない。
    /// Warning を残して <see cref="FillerOutcome.Failed"/> を返し、呼び出し元はその再生の間フィラーを使わない。
    /// デバイス自体の故障なら、続く本編セグメントの再生で例外として表に出る。
    /// ただし再生のタイムアウト (<see cref="TimeoutException"/>) と、デバイスのエラー (<see cref="NAudio.MmException"/>) は握らずに投げる。
    /// どちらもデバイスが音を読まなくなったということで、本編も鳴らせないため。
    /// </remarks>
    private async Task<FillerOutcome> TryPlayFillerAsync(
        PlaybackStream stream,
        Func<CancellationToken, Task<byte[]?>> getFillerAudio,
        bool isFirstPlayback,
        TimeSpan leadingSilence,
        CancellationToken cancellationToken)
    {
        try
        {
            var fillerAudio = await getFillerAudio(cancellationToken);
            if (fillerAudio == null || fillerAudio.Length == 0)
            {
                return FillerOutcome.Skipped;
            }
            _logger.LogDebug("フィラー音声を挿入 (size: {Size} bytes)", fillerAudio.Length);
            await PlayAsync(stream, fillerAudio, isFirstPlayback, leadingSilence, cancellationToken);
            return FillerOutcome.Played;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not (TimeoutException or NAudio.MmException))
        {
            _logger.LogWarning(ex, "フィラー音声を再生できませんでした。この読み上げではフィラーを使いません");
            return FillerOutcome.Failed;
        }
    }

    /// <summary>
    /// 1 つの音声 (WAV / Ogg Opus) を流れに並べ、デバイスが読み終えるまで待つ。前に間の無音を置く。
    /// </summary>
    /// <param name="isFirst">読み上げの最初の音か (前に置く無音が違う)</param>
    /// <param name="leadingSilence">最初の音の前に足す無音 (<see cref="Configuration.DeviceOptionSettings.LeadingSilenceMs"/>)</param>
    private async Task PlayAsync(PlaybackStream stream, byte[] audioData, bool isFirst, TimeSpan leadingSilence, CancellationToken cancellationToken)
    {
        var silence = isFirst
            ? TimeSpan.FromMilliseconds(AudioConstants.StartSilenceMs) + leadingSilence
            : TimeSpan.FromMilliseconds(AudioConstants.GapSilenceMs);

        float[] samples;
        using (var reader = _formatDetector.CreateWaveStream(audioData))
        {
            samples = [.. PlaybackStream.Silence(silence), .. PlaybackStream.Decode(reader.ToSampleProvider())];
        }
        _logger.LogDebug("音声を流します - 長さ: {Duration}ms (前の無音 {Silence}ms を含む)",
            PlaybackStream.DurationOf(samples.Length).TotalMilliseconds, silence.TotalMilliseconds);

        // WaitAsync はキャンセルなら OperationCanceledException、時間切れなら TimeoutException を投げ、どちらでもタイマーを破棄する
        // (デバイスが読まなくなったとき。長さ + 余裕を待っても読み終わらなければ諦める)
        var timeout = PlaybackStream.DurationOf(samples.Length) + TimeSpan.FromMilliseconds(AudioConstants.PlaybackTimeoutMarginMs);
        var played = stream.Enqueue(samples);
        try
        {
            await played.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException) when (!played.IsCompleted)
        {
            _logger.LogWarning("セグメント再生がタイムアウトしました。強制停止します。");
            throw new TimeoutException("セグメント再生がタイムアウトしました");
        }
    }
}
