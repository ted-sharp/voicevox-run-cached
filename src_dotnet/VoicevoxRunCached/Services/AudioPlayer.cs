using Microsoft.Extensions.Logging;
using NAudio.Wave;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Constants;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services.Audio;

namespace VoicevoxRunCached.Services;

/// <summary>
/// 音声再生の統合制御を行うクラス
/// 単一音声、セグメント順次再生、フィラー挿入、音声生成待機を統合
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
    /// 音声データをストリーミング再生します
    /// </summary>
    public async Task PlayAudioStreamingAsync(byte[] audioData, Func<byte[], Task>? cacheCallback = null, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AudioPlayer));

        await _deviceManager.EnsureDeviceReadyAsync();
        await PlayAudioInternalAsync(audioData, cacheCallback, cancellationToken);
    }

    /// <summary>
    /// 音声データを再生します
    /// </summary>
    public async Task PlayAudioAsync(byte[] audioData, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AudioPlayer));

        await _deviceManager.EnsureDeviceReadyAsync();
        await PlayAudioInternalAsync(audioData, null, cancellationToken);
    }

    /// <summary>
    /// 音声セグメントのリストを順次再生します
    /// </summary>
    public async Task PlayAudioSequentiallyAsync(List<byte[]> audioSegments, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AudioPlayer));

        await _deviceManager.EnsureDeviceReadyAsync();

        try
        {
            var wavePlayer = _wavePlayerManager.GetOrCreateSharedWavePlayer();
            _currentWavePlayer = wavePlayer;

            foreach (var segment in audioSegments)
            {
                if (segment.Length == 0)
                    continue;

                await PlaySegmentAsync(segment, false, cancellationToken);
            }
        }
        finally
        {
            StopAudio();
        }
    }

    /// <summary>
    /// 事前合成済みのセグメントを順次再生し、必要に応じてフィラー音声を挿入する。
    /// </summary>
    public async Task PlaySegmentsAsync(
        List<TextSegment> segments,
        FillerManager? fillerManager = null,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AudioPlayer));

        _logger.LogDebug("PlaySegmentsAsync 開始 - セグメント数: {SegmentCount}, フィラーマネージャー: {HasFillerManager}",
            segments.Count, fillerManager != null);

        await _deviceManager.EnsureDeviceReadyAsync();

        try
        {
            var wavePlayer = _wavePlayerManager.GetOrCreateSharedWavePlayer();
            _currentWavePlayer = wavePlayer;

            bool isFirstSegment = true;

            for (int i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];
                _logger.LogDebug("セグメント {SegmentNumber}/{Total} を処理中: \"{Text}\" (キャッシュ済み: {IsCached})",
                    i + 1, segments.Count, segment.Text, segment.IsCached);

                if (segment.AudioData == null || segment.AudioData.Length == 0)
                {
                    _logger.LogWarning("セグメント {SegmentIndex} に音声データがありません。スキップします: {Text}",
                        i + 1, segment.Text);
                    continue;
                }

                await PlaySegmentAsync(segment.AudioData, isFirstSegment, cancellationToken);
                isFirstSegment = false;

                var fillerAudio = await CheckAndGetFillerAsync(i, segments, fillerManager, cancellationToken);
                if (fillerAudio != null)
                {
                    _logger.LogDebug("フィラー音声を再生します (サイズ: {Size} bytes)", fillerAudio.Length);
                    await PlaySegmentAsync(fillerAudio, false, cancellationToken);
                }

                if (i < segments.Count - 1)
                {
                    await Task.Delay(AudioConstants.InterSegmentGapMs, cancellationToken);
                }
            }

            _logger.LogDebug("全セグメントの再生が完了しました");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PlaySegmentsAsync でエラーが発生しました");
            throw new InvalidOperationException("音声シーケンシャル再生中にエラーが発生しました", ex);
        }
        finally
        {
            StopAudio();
        }
    }

    /// <summary>
    /// 音声再生を停止します
    /// </summary>
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

    /// <summary>
    /// 利用可能なデバイスの一覧を取得します
    /// </summary>
    public static List<string> GetAvailableDevices()
    {
        return AudioDeviceManager.GetAvailableDevices();
    }

    #region Private Methods

    private async Task PlayAudioInternalAsync(byte[] audioData, Func<byte[], Task>? cacheCallback, CancellationToken cancellationToken)
    {
        WaveStream? reader = null;
        try
        {
            StopAudio();

            reader = await _formatDetector.CreateWaveStreamAsync(audioData);
            _currentWavePlayer = _wavePlayerManager.CreateWavePlayer();

            var tcs = new TaskCompletionSource<bool>();

            // キャッシュ保存を並行で開始
            Task? cacheTask = null;
            if (cacheCallback != null)
            {
                cacheTask = Task.Run(async () =>
                {
                    try
                    {
                        await cacheCallback(audioData);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "音声のキャッシュ保存に失敗しました");
                    }
                });
            }

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
                if (_currentWavePlayer != null && handler != null)
                {
                    _currentWavePlayer.PlaybackStopped -= handler;
                }
            };
            _currentWavePlayer.PlaybackStopped += handler;

            _currentWavePlayer.Init(reader);
            await Task.Delay(AudioConstants.SubsequentSegmentDelayMs, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            _currentWavePlayer.Play();

            using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
            {
                await tcs.Task.ConfigureAwait(false);
            }

            await Task.Delay(AudioConstants.BufferFlushDelayMs, cancellationToken);

            if (cacheTask != null)
            {
                await cacheTask;
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to play audio: {ex.Message}", ex);
        }
        finally
        {
            try
            { reader?.Dispose(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to dispose audio reader"); }

            StopAudio();
        }
    }

    private async Task PlaySegmentAsync(byte[] audioData, bool isFirstSegment, CancellationToken cancellationToken)
    {
        WaveStream? reader = null;
        try
        {
            _logger.LogDebug("PlaySegmentAsync 開始 - サイズ: {Size} bytes, 最初のセグメント: {IsFirst}",
                audioData.Length, isFirstSegment);

            reader = await _formatDetector.CreateWaveStreamAsync(audioData);

            var tcs = new TaskCompletionSource<bool>();

            if (_currentWavePlayer != null)
            {
                EventHandler<StoppedEventArgs>? handler = null;
                handler = (_, e) =>
                {
                    _logger.LogDebug("PlaybackStopped イベントが発生しました - 例外: {Exception}",
                        e.Exception?.Message ?? "なし");
                    if (e.Exception != null)
                    {
                        tcs.TrySetException(e.Exception);
                    }
                    else
                    {
                        tcs.TrySetResult(true);
                    }
                    if (_currentWavePlayer != null && handler != null)
                    {
                        _currentWavePlayer.PlaybackStopped -= handler;
                    }
                };
                _currentWavePlayer.PlaybackStopped += handler;
            }

            _currentWavePlayer?.Init(reader);

            // セグメントタイプに応じた遅延
            if (isFirstSegment)
            {
                await Task.Delay(AudioConstants.FirstSegmentDelayMs, cancellationToken);
            }
            else
            {
                await Task.Delay(AudioConstants.SubsequentSegmentDelayMs, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            _currentWavePlayer?.Play();

            using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
            {
                var timeoutTask = Task.Delay(TimeSpan.FromMilliseconds(AudioConstants.DefaultPlaybackTimeoutMs), cancellationToken);
                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    _logger.LogWarning("セグメント再生がタイムアウトしました。強制停止します。");
                    _currentWavePlayer?.Stop();
                    throw new TimeoutException("セグメント再生がタイムアウトしました");
                }

                await tcs.Task;
            }

            // 再生完了後の遅延
            var playbackDelay = isFirstSegment
                ? AudioConstants.FirstSegmentTrailingDelayMs
                : AudioConstants.SubsequentSegmentTrailingDelayMs;
            await Task.Delay(playbackDelay, cancellationToken);

            _currentWavePlayer?.Stop();

            _logger.LogDebug("セグメント再生が完了しました (遅延: {Delay}ms)", playbackDelay);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "セグメント再生中にエラーが発生しました");
            throw new InvalidOperationException($"Failed to play audio segment: {ex.Message}", ex);
        }
        finally
        {
            try
            { reader?.Dispose(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to dispose audio reader"); }
        }
    }

    private async Task<byte[]?> CheckAndGetFillerAsync(
        int currentIndex,
        List<TextSegment> segments,
        FillerManager? fillerManager,
        CancellationToken cancellationToken)
    {
        if (fillerManager == null)
        {
            _logger.LogDebug("フィラーマネージャーが設定されていないため、フィラーは挿入されません");
            return null;
        }

        if (currentIndex >= segments.Count - 1)
        {
            _logger.LogDebug("最後のセグメントのため、フィラーは挿入されません");
            return null;
        }

        var nextSegment = segments[currentIndex + 1];
        bool nextSegmentReady = nextSegment.IsCached && nextSegment.AudioData != null && nextSegment.AudioData.Length > 0;

        if (!nextSegmentReady)
        {
            try
            {
                _logger.LogDebug("次のセグメントの準備が間に合わないため、フィラー音声を取得します");
                var fillerAudio = await fillerManager.GetRandomFillerAudioAsync();
                if (fillerAudio != null)
                {
                    _logger.LogDebug("フィラー音声を取得しました (サイズ: {Size} bytes)", fillerAudio.Length);
                    return fillerAudio;
                }
                else
                {
                    _logger.LogDebug("フィラー音声の取得に失敗しました");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "フィラー音声の取得中にエラーが発生しました");
            }
        }
        else
        {
            _logger.LogDebug("次のセグメントは既に準備完了のため、フィラーは挿入されません (サイズ: {Size} bytes)",
                nextSegment.AudioData?.Length ?? 0);
        }

        return null;
    }

    #endregion
}
