using NAudio.Wave;
using Serilog;
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
    private bool _disposed;
    private IWavePlayer? _currentWavePlayer;

    public AudioPlayer(AudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;
        _formatDetector = new AudioFormatDetector();
        _deviceManager = new AudioDeviceManager(settings);
        _wavePlayerManager = new WavePlayerManager(settings);

        Log.Information("AudioPlayer を初期化しました - 音量: {Volume}, デバイス: {Device}",
            settings.Volume, settings.OutputDevice);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    ~AudioPlayer()
    {
        Dispose(false);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                try
                {
                    StopAudio();
                    _deviceManager.Dispose();
                    _wavePlayerManager.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "AudioPlayerの破棄中にエラーが発生しました");
                }
            }

            _disposed = true;
        }
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
    /// 音声セグメントのリストを順次再生し、必要に応じて音声生成を行います
    /// </summary>
    public async Task PlayAudioSequentiallyWithGenerationAsync(
        List<TextSegment> segments,
        AudioProcessingChannel? processingChannel,
        FillerManager? fillerManager = null,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AudioPlayer));

        Log.Information("PlayAudioSequentiallyWithGenerationAsync 開始 - セグメント数: {SegmentCount}, フィラーマネージャー: {HasFillerManager}",
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
                Log.Information("セグメント {SegmentNumber}/{Total} を処理中: \"{Text}\" (キャッシュ済み: {IsCached})",
                    i + 1, segments.Count, segment.Text, segment.IsCached);

                // 未キャッシュセグメントの音声生成を待機
                if (!segment.IsCached || segment.AudioData == null)
                {
                    await WaitForSegmentGenerationAsync(segment, i, processingChannel, cancellationToken);
                }

                // セグメントを再生
                await PlaySegmentAsync(segment.AudioData!, isFirstSegment, cancellationToken);
                isFirstSegment = false;

                // フィラーの挿入をチェック
                var fillerAudio = await CheckAndGetFillerAsync(i, segments, fillerManager, cancellationToken);
                if (fillerAudio != null)
                {
                    Log.Information("フィラー音声を再生します (サイズ: {Size} bytes)", fillerAudio.Length);
                    await PlaySegmentAsync(fillerAudio, false, cancellationToken);
                }

                // セグメント間の間隔を確保
                if (i < segments.Count - 1)
                {
                    await Task.Delay(50, cancellationToken);
                }
            }

            Log.Information("全セグメントの再生が完了しました");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PlayAudioSequentiallyWithGenerationAsync でエラーが発生しました");
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
            _currentWavePlayer?.Dispose();
            _currentWavePlayer = null;
            _wavePlayerManager.StopSharedWavePlayer();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "音声停止中にエラーが発生しました");
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
                        Log.Warning(ex, "音声のキャッシュ保存に失敗しました");
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
            catch (Exception ex) { Log.Debug(ex, "Failed to dispose audio reader"); }

            StopAudio();
        }
    }

    private async Task PlaySegmentAsync(byte[] audioData, bool isFirstSegment, CancellationToken cancellationToken)
    {
        WaveStream? reader = null;
        try
        {
            Log.Debug("PlaySegmentAsync 開始 - サイズ: {Size} bytes, 最初のセグメント: {IsFirst}",
                audioData.Length, isFirstSegment);

            reader = await _formatDetector.CreateWaveStreamAsync(audioData);

            var tcs = new TaskCompletionSource<bool>();

            if (_currentWavePlayer != null)
            {
                EventHandler<StoppedEventArgs>? handler = null;
                handler = (_, e) =>
                {
                    Log.Debug("PlaybackStopped イベントが発生しました - 例外: {Exception}",
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
                    Log.Warning("セグメント再生がタイムアウトしました。強制停止します。");
                    _currentWavePlayer?.Stop();
                    throw new TimeoutException("セグメント再生がタイムアウトしました");
                }

                await tcs.Task;
            }

            // 再生完了後の遅延
            var playbackDelay = isFirstSegment ? 150 : 100;
            await Task.Delay(playbackDelay, cancellationToken);

            _currentWavePlayer?.Stop();

            Log.Information("セグメント再生が完了しました (遅延: {Delay}ms)", playbackDelay);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "セグメント再生中にエラーが発生しました");
            throw new InvalidOperationException($"Failed to play audio segment: {ex.Message}", ex);
        }
        finally
        {
            try
            { reader?.Dispose(); }
            catch (Exception ex) { Log.Debug(ex, "Failed to dispose audio reader"); }
        }
    }

    private async Task WaitForSegmentGenerationAsync(
        TextSegment segment,
        int segmentIndex,
        AudioProcessingChannel? processingChannel,
        CancellationToken cancellationToken)
    {
        if (segment.IsCached && segment.AudioData != null)
        {
            Log.Debug("セグメント {SegmentIndex} は既にキャッシュ済みです", segmentIndex + 1);
            return;
        }

        if (processingChannel != null)
        {
            try
            {
                Log.Information("セグメント {SegmentIndex} の音声生成を開始します", segmentIndex + 1);

                var segmentRequest = new VoiceRequest
                {
                    Text = segment.Text,
                    SpeakerId = segment.SpeakerId ?? 1,
                    Speed = 1.0,
                    Pitch = 0.0,
                    Volume = 1.0
                };

                var result = await processingChannel.ProcessAudioAsync(segmentRequest, cancellationToken);
                if (result.Success && result.AudioData.Length > 0)
                {
                    segment.AudioData = result.AudioData;
                    segment.IsCached = true;
                    Log.Information("セグメント {SegmentIndex} の生成が完了しました (サイズ: {Size} bytes)",
                        segmentIndex + 1, result.AudioData.Length);
                }
                else
                {
                    Log.Warning("セグメント {SegmentIndex} の生成に失敗しました: {Error}",
                        segmentIndex + 1, result.ErrorMessage ?? "Unknown error");
                    throw new InvalidOperationException($"Failed to generate audio for segment {segmentIndex + 1}: {result.ErrorMessage}");
                }
            }
            catch (OperationCanceledException)
            {
                Log.Information("セグメント {SegmentIndex} の生成がキャンセルされました", segmentIndex + 1);
                throw;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "セグメント {SegmentIndex} の生成に失敗しました", segmentIndex + 1);
                throw new InvalidOperationException($"Failed to generate audio for segment {segmentIndex + 1}", ex);
            }
        }
        else
        {
            // フォールバック処理
            var waitStartTime = DateTime.UtcNow;
            const int maxWaitTimeMs = 30000;

            Log.Information("フォールバック処理: セグメント {SegmentIndex} の準備完了を待機中...", segmentIndex + 1);

            while ((!segment.IsCached || segment.AudioData == null) &&
                   (DateTime.UtcNow - waitStartTime).TotalMilliseconds < maxWaitTimeMs)
            {
                await Task.Delay(100, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }

            if ((DateTime.UtcNow - waitStartTime).TotalMilliseconds >= maxWaitTimeMs)
            {
                Log.Warning("セグメント {SegmentIndex} の待機がタイムアウトしました", segmentIndex + 1);
                throw new TimeoutException($"Segment {segmentIndex + 1} generation timeout after {maxWaitTimeMs}ms");
            }

            if (segment.AudioData == null)
            {
                Log.Warning("セグメント {SegmentIndex} の生成に失敗しました", segmentIndex + 1);
                throw new InvalidOperationException($"Segment {segmentIndex + 1} generation failed");
            }

            Log.Information("セグメント {SegmentIndex} の準備が完了しました (サイズ: {Size} bytes)",
                segmentIndex + 1, segment.AudioData.Length);
        }
    }

    private static async Task<byte[]?> CheckAndGetFillerAsync(
        int currentIndex,
        List<TextSegment> segments,
        FillerManager? fillerManager,
        CancellationToken cancellationToken)
    {
        if (fillerManager == null)
        {
            Log.Debug("フィラーマネージャーが設定されていないため、フィラーは挿入されません");
            return null;
        }

        if (currentIndex >= segments.Count - 1)
        {
            Log.Debug("最後のセグメントのため、フィラーは挿入されません");
            return null;
        }

        var nextSegment = segments[currentIndex + 1];
        bool nextSegmentReady = nextSegment.IsCached && nextSegment.AudioData != null && nextSegment.AudioData.Length > 0;

        if (!nextSegmentReady)
        {
            try
            {
                Log.Debug("次のセグメントの準備が間に合わないため、フィラー音声を取得します");
                var fillerAudio = await fillerManager.GetRandomFillerAudioAsync();
                if (fillerAudio != null)
                {
                    Log.Information("フィラー音声を取得しました (サイズ: {Size} bytes)", fillerAudio.Length);
                    return fillerAudio;
                }
                else
                {
                    Log.Debug("フィラー音声の取得に失敗しました");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "フィラー音声の取得中にエラーが発生しました");
            }
        }
        else
        {
            Log.Debug("次のセグメントは既に準備完了のため、フィラーは挿入されません (サイズ: {Size} bytes)",
                nextSegment.AudioData?.Length ?? 0);
        }

        return null;
    }

    #endregion
}
