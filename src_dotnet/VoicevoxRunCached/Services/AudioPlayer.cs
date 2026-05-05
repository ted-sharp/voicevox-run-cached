using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Constants;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services.Audio;

namespace VoicevoxRunCached.Services;

/// <summary>
/// 音声再生の統合制御。単一音声再生・セグメント順次再生・フィラー挿入を統合する。
/// </summary>
public class AudioPlayer : IDisposable
{
    private readonly AudioDeviceManager _deviceManager;
    private readonly AudioFormatDetector _formatDetector;
    private readonly AudioSettings _settings;
    private readonly WavePlayerManager _wavePlayerManager;
    private readonly ILogger _logger;
    private bool _disposed;
    private IWavePlayer? _currentWavePlayer;

    public AudioPlayer(
        AudioSettings settings,
        AudioDeviceManager deviceManager,
        AudioFormatDetector formatDetector,
        WavePlayerManager wavePlayerManager,
        ILogger logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
        _formatDetector = formatDetector ?? throw new ArgumentNullException(nameof(formatDetector));
        _wavePlayerManager = wavePlayerManager ?? throw new ArgumentNullException(nameof(wavePlayerManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _logger.LogDebug("AudioPlayer を初期化しました - 音量: {Volume}, デバイス: {Device}",
            settings.Volume, settings.OutputDevice);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        try
        {
            StopAudio();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AudioPlayer の停止中にエラーが発生しました");
        }

        _disposed = true;
    }

    /// <summary>
    /// 単独の音声データを再生する。キャッシュ書き込みを並行で動かす場合は cacheCallback を渡す。
    /// 各呼び出しごとに新しい WavePlayer を生成する (共有しない)。
    /// </summary>
    public async Task PlayAudioStreamingAsync(byte[] audioData, Func<byte[], Task>? cacheCallback = null, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AudioPlayer));

        await _deviceManager.EnsureDeviceReadyAsync();

        Task? cacheTask = null;
        if (cacheCallback != null)
        {
            cacheTask = Task.Run(async () =>
            {
                try
                { await cacheCallback(audioData); }
                catch (Exception ex) { _logger.LogWarning(ex, "音声のキャッシュ保存に失敗しました"); }
            });
        }

        try
        {
            StopAudio();
            _currentWavePlayer = _wavePlayerManager.CreateWavePlayer();

            await PlayWaveAsync(
                audioData,
                _currentWavePlayer,
                leadingDelayMs: AudioConstants.SubsequentSegmentDelayMs,
                trailingDelayMs: AudioConstants.BufferFlushDelayMs,
                useTimeout: false,
                cancellationToken);

            if (cacheTask != null)
            {
                await cacheTask;
            }
        }
        finally
        {
            StopAudio();
        }
    }

    /// <summary>
    /// 単独の音声データを既存デバイスで再生する。
    /// </summary>
    public Task PlayAudioAsync(byte[] audioData, CancellationToken cancellationToken = default)
        => PlayAudioStreamingAsync(audioData, cacheCallback: null, cancellationToken);

    /// <summary>
    /// 複数の音声セグメントを順次再生する (フィラー挿入なし)。
    /// </summary>
    public async Task PlayAudioSequentiallyAsync(List<byte[]> audioSegments, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AudioPlayer));

        await _deviceManager.EnsureDeviceReadyAsync();

        try
        {
            _currentWavePlayer = _wavePlayerManager.GetOrCreateSharedWavePlayer();

            foreach (var segment in audioSegments)
            {
                if (segment.Length == 0)
                    continue;

                await PlaySharedSegmentAsync(segment, isFirstSegment: false, cancellationToken);
            }
        }
        finally
        {
            StopAudio();
        }
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
            _currentWavePlayer = _wavePlayerManager.GetOrCreateSharedWavePlayer();

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

                    await PlaySharedSegmentAsync(segment.AudioData, isFirstPlayback, cancellationToken);
                    isFirstPlayback = false;
                    continue;
                }

                // 次のセグメントが未到着 — フィラー閾値だけ待機
                var waitTask = reader.WaitToReadAsync(cancellationToken).AsTask();
                var fillerTask = Task.Delay(AudioConstants.FillerStartDelayMs, cancellationToken);
                var done = await Task.WhenAny(waitTask, fillerTask);

                if (done == fillerTask)
                {
                    var fillerAudio = fillerManager != null
                        ? await fillerManager.GetRandomFillerAudioAsync()
                        : null;
                    if (fillerAudio != null && fillerAudio.Length > 0)
                    {
                        _logger.LogDebug("フィラー音声を挿入 (size: {Size} bytes)", fillerAudio.Length);
                        await PlaySharedSegmentAsync(fillerAudio, isFirstPlayback, cancellationToken);
                        isFirstPlayback = false;
                    }
                    continue;
                }

                if (!await waitTask)
                {
                    break;
                }
            }

            _logger.LogDebug("ストリーミング再生が完了しました - 再生数: {Count}", playedCount);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PlayStreamingAsync でエラーが発生しました");
            throw new InvalidOperationException("ストリーミング再生中にエラーが発生しました", ex);
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
            _currentWavePlayer?.Stop();
            _currentWavePlayer = null;
            _wavePlayerManager.StopSharedWavePlayer();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "音声停止中にエラーが発生しました");
        }
    }

    private Task PlaySharedSegmentAsync(byte[] audioData, bool isFirstSegment, CancellationToken cancellationToken)
    {
        if (_currentWavePlayer == null)
        {
            throw new InvalidOperationException("共有 WavePlayer が初期化されていません");
        }

        var leadingDelayMs = isFirstSegment
            ? AudioConstants.FirstSegmentDelayMs
            : AudioConstants.SubsequentSegmentDelayMs;
        var trailingDelayMs = isFirstSegment
            ? AudioConstants.FirstSegmentTrailingDelayMs
            : AudioConstants.SubsequentSegmentTrailingDelayMs;

        return PlayWaveAsync(
            audioData,
            _currentWavePlayer,
            leadingDelayMs,
            trailingDelayMs,
            useTimeout: true,
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
        bool useTimeout,
        CancellationToken cancellationToken)
    {
        WaveStream? reader = null;
        try
        {
            _logger.LogDebug("PlayWaveAsync 開始 - サイズ: {Size} bytes, タイムアウト: {Timeout}",
                audioData.Length, useTimeout);

            reader = await _formatDetector.CreateWaveStreamAsync(audioData);

            var tcs = new TaskCompletionSource<bool>();
            EventHandler<StoppedEventArgs>? handler = null;
            handler = (_, e) =>
            {
                if (e.Exception != null)
                {
                    tcs.TrySetException(e.Exception);
                }
                else
                {
                    tcs.TrySetResult(true);
                }

                wavePlayer.PlaybackStopped -= handler!;
            };
            wavePlayer.PlaybackStopped += handler;

            wavePlayer.Init(reader);
            await Task.Delay(leadingDelayMs, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            wavePlayer.Play();

            await using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

            if (useTimeout)
            {
                var timeoutTask = Task.Delay(TimeSpan.FromMilliseconds(AudioConstants.DefaultPlaybackTimeoutMs), cancellationToken);
                var completed = await Task.WhenAny(tcs.Task, timeoutTask);
                if (completed == timeoutTask)
                {
                    _logger.LogWarning("セグメント再生がタイムアウトしました。強制停止します。");
                    wavePlayer.Stop();
                    throw new TimeoutException("セグメント再生がタイムアウトしました");
                }
            }

            await tcs.Task;

            await Task.Delay(trailingDelayMs, cancellationToken);

            if (useTimeout)
            {
                wavePlayer.Stop();
            }

            _logger.LogDebug("音声再生が完了しました (trailing: {Delay}ms)", trailingDelayMs);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "音声再生中にエラーが発生しました");
            throw new InvalidOperationException($"Failed to play audio: {ex.Message}", ex);
        }
        finally
        {
            try
            { reader?.Dispose(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to dispose audio reader"); }
        }
    }

}
