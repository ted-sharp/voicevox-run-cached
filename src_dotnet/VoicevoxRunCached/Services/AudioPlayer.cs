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
            _currentWavePlayer = _wavePlayerManager.GetOrCreateSharedWavePlayer();

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

                await PlaySharedSegmentAsync(segment.AudioData, isFirstSegment, cancellationToken);
                isFirstSegment = false;

                var fillerAudio = await CheckAndGetFillerAsync(i, segments, fillerManager, cancellationToken);
                if (fillerAudio != null)
                {
                    _logger.LogDebug("フィラー音声を再生します (サイズ: {Size} bytes)", fillerAudio.Length);
                    await PlaySharedSegmentAsync(fillerAudio, isFirstSegment: false, cancellationToken);
                }

                if (i < segments.Count - 1)
                {
                    await Task.Delay(AudioConstants.InterSegmentGapMs, cancellationToken);
                }
            }

            _logger.LogDebug("全セグメントの再生が完了しました");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "PlaySegmentsAsync でエラーが発生しました");
            throw new InvalidOperationException("音声シーケンシャル再生中にエラーが発生しました", ex);
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

        if (nextSegmentReady)
        {
            _logger.LogDebug("次のセグメントは既に準備完了のため、フィラーは挿入されません (サイズ: {Size} bytes)",
                nextSegment.AudioData?.Length ?? 0);
            return null;
        }

        try
        {
            _logger.LogDebug("次のセグメントの準備が間に合わないため、フィラー音声を取得します");
            var fillerAudio = await fillerManager.GetRandomFillerAudioAsync();
            if (fillerAudio != null)
            {
                _logger.LogDebug("フィラー音声を取得しました (サイズ: {Size} bytes)", fillerAudio.Length);
            }
            return fillerAudio;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "フィラー音声の取得中にエラーが発生しました");
            return null;
        }
    }
}
