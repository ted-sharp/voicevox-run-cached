using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using VoicevoxRunCached.Constants;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services.Audio;

/// <summary>
/// 音声再生の統合制御。Channel 経由で渡されるセグメントを順次再生し、合成待ち中はフィラー音声で隙間を埋める。
/// </summary>
/// <remarks>
/// 共有 <c>WavePlayer</c> を使い回すことでセグメント間のデバイス open/close コストを排除し、
/// 連続再生時の無音ギャップを最小化する。Dispose 時にまとめて停止する。
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
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AudioPlayer));

        _logger.LogDebug("PlayStreamingAsync 開始 - デバイス: {Device}, フィラー: {HasFiller}, 待ち 1 回の上限: {MaxFillersPerWait}",
            device, getFillerAudio != null, maxFillersPerWait);

        await _deviceManager.EnsureDeviceReadyAsync(device);

        try
        {
            var wavePlayer = _wavePlayerManager.GetOrCreateSharedWavePlayer(device);

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

                    await PlaySharedSegmentAsync(wavePlayer, segment.AudioData, isFirstPlayback, cancellationToken);
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
                    var filler = await TryPlayFillerAsync(wavePlayer, fillerSource, isFirstPlayback, cancellationToken);
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

            _logger.LogDebug("ストリーミング再生が完了しました - 再生数: {Count}", playedCount);
        }
        finally
        {
            StopAudio();
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
    /// ただし再生のタイムアウト (<see cref="TimeoutException"/>) は握らずに投げる。<see cref="PlayWaveAsync"/> は
    /// 再生の途中で共有 WavePlayer を Stop() しており、そのまま本編を再生すると、遅れて届く停止イベントで本編が途中で切れうるため
    /// (デバイスの異常として表に出す)。
    /// </remarks>
    private async Task<FillerOutcome> TryPlayFillerAsync(
        IWavePlayer wavePlayer,
        Func<CancellationToken, Task<byte[]?>> getFillerAudio,
        bool isFirstPlayback,
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
            await PlaySharedSegmentAsync(wavePlayer, fillerAudio, isFirstPlayback, cancellationToken);
            return FillerOutcome.Played;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not TimeoutException)
        {
            _logger.LogWarning(ex, "フィラー音声を再生できませんでした。この読み上げではフィラーを使いません");
            return FillerOutcome.Failed;
        }
    }

    private Task PlaySharedSegmentAsync(IWavePlayer wavePlayer, byte[] audioData, bool isFirstSegment, CancellationToken cancellationToken)
    {
        var leadingDelayMs = isFirstSegment
            ? AudioConstants.FirstSegmentDelayMs
            : AudioConstants.SubsequentSegmentDelayMs;
        var trailingDelayMs = isFirstSegment
            ? AudioConstants.FirstSegmentTrailingDelayMs
            : AudioConstants.SubsequentSegmentTrailingDelayMs;

        return PlayWaveAsync(
            audioData,
            wavePlayer,
            leadingDelayMs,
            trailingDelayMs,
            cancellationToken);
    }

    /// <summary>
    /// 1 つの WAV/Ogg Opus データを指定 WavePlayer で再生する共通ロジック。
    /// </summary>
    private async Task PlayWaveAsync(
        byte[] audioData,
        IWavePlayer wavePlayer,
        int leadingDelayMs,
        int trailingDelayMs,
        CancellationToken cancellationToken)
    {
        WaveStream? reader = null;
        // 続きを NAudio の PlaybackStopped のスレッドでそのまま走らせない (次の文の Init / Play がイベントの処理中に走るため)
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception != null)
            {
                tcs.TrySetException(e.Exception);
            }
            else
            {
                tcs.TrySetResult(true);
            }
        }

        wavePlayer.PlaybackStopped += OnPlaybackStopped;
        try
        {
            _logger.LogDebug("PlayWaveAsync 開始 - サイズ: {Size} bytes", audioData.Length);

            reader = _formatDetector.CreateWaveStream(audioData);

            // セグメントごとに Init し直すので、サンプルレートの違う音声 (合成 24kHz・キャッシュ 48kHz) が混在してよい
            wavePlayer.Init(reader);
            await Task.Delay(leadingDelayMs, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            wavePlayer.Play();

            // WaitAsync はキャンセルなら OperationCanceledException、時間切れなら TimeoutException を投げ、どちらでもタイマーを破棄する
            var timeout = reader.TotalTime + TimeSpan.FromMilliseconds(AudioConstants.PlaybackTimeoutMarginMs);
            try
            {
                await tcs.Task.WaitAsync(timeout, cancellationToken);
            }
            catch (TimeoutException) when (!tcs.Task.IsCompleted)
            {
                _logger.LogWarning("セグメント再生がタイムアウトしました。強制停止します。");
                wavePlayer.Stop();
                throw new TimeoutException("セグメント再生がタイムアウトしました");
            }

            await Task.Delay(trailingDelayMs, cancellationToken);
            wavePlayer.Stop();

            _logger.LogDebug("音声再生が完了しました (trailing: {Delay}ms)", trailingDelayMs);
        }
        finally
        {
            // Init/Play/Delay のいずれの段階で失敗しても確実に handler を外す
            // (共有 WavePlayer の次回再生時に古いハンドラが発火するのを防ぐ)
            wavePlayer.PlaybackStopped -= OnPlaybackStopped;
            reader?.Dispose();
        }
    }
}
