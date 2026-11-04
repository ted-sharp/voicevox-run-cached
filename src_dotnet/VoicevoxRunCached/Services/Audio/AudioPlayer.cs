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
    /// セグメントが届くたびに順次再生する。次のセグメントが <see cref="AudioConstants.FillerStartDelayMs"/>
    /// 以内に届かない場合はフィラー音声で隙間を埋める。
    /// </summary>
    /// <remarks>
    /// 呼び出し元 (producer) が <see cref="ChannelWriter{T}.Complete"/> するまでループする。
    /// AudioData が null のセグメントは合成失敗扱いで読み飛ばす。
    /// </remarks>
    public async Task PlayStreamingAsync(
        ChannelReader<TextSegment> reader,
        FillerManager? fillerManager = null,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AudioPlayer));

        _logger.LogDebug("PlayStreamingAsync 開始 - フィラーマネージャー: {HasFillerManager}", fillerManager != null);

        await _deviceManager.EnsureDeviceReadyAsync();

        try
        {
            var wavePlayer = _wavePlayerManager.GetOrCreateSharedWavePlayer();

            bool isFirstPlayback = true;
            int playedCount = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (reader.TryRead(out var segment))
                {
                    playedCount++;

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

                // 次のセグメントが未到着 — フィラー閾値内に届かなければフィラーを挿入する
                var waitOutcome = await WaitForNextSegmentOrFillerTimeoutAsync(reader, cancellationToken);
                if (waitOutcome == NextSegmentWait.NoMore)
                {
                    break;
                }
                if (waitOutcome == NextSegmentWait.FillerTimeout
                    && await TryPlayFillerAsync(wavePlayer, fillerManager, isFirstPlayback, cancellationToken))
                {
                    isFirstPlayback = false;
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

    /// <summary>
    /// フィラー音声を再生する。フィラーが無効/未取得時は何もせず false を返す。
    /// </summary>
    private async Task<bool> TryPlayFillerAsync(IWavePlayer wavePlayer, FillerManager? fillerManager, bool isFirstPlayback, CancellationToken cancellationToken)
    {
        var fillerAudio = fillerManager != null
            ? await fillerManager.GetRandomFillerAudioAsync(cancellationToken)
            : null;
        if (fillerAudio == null || fillerAudio.Length == 0)
        {
            return false;
        }
        _logger.LogDebug("フィラー音声を挿入 (size: {Size} bytes)", fillerAudio.Length);
        await PlaySharedSegmentAsync(wavePlayer, fillerAudio, isFirstPlayback, cancellationToken);
        return true;
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
    /// 1 つの WAV/MP3 データを指定 WavePlayer で再生する共通ロジック。
    /// </summary>
    private async Task PlayWaveAsync(
        byte[] audioData,
        IWavePlayer wavePlayer,
        int leadingDelayMs,
        int trailingDelayMs,
        CancellationToken cancellationToken)
    {
        WaveStream? reader = null;
        var tcs = new TaskCompletionSource<bool>();

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

            wavePlayer.Init(reader);
            await Task.Delay(leadingDelayMs, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            wavePlayer.Play();

            await using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

            var timeout = reader.TotalTime + TimeSpan.FromMilliseconds(AudioConstants.PlaybackTimeoutMarginMs);
            var timeoutTask = Task.Delay(timeout, cancellationToken);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask);
            if (completed == timeoutTask)
            {
                _logger.LogWarning("セグメント再生がタイムアウトしました。強制停止します。");
                wavePlayer.Stop();
                throw new TimeoutException("セグメント再生がタイムアウトしました");
            }

            await tcs.Task;

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
