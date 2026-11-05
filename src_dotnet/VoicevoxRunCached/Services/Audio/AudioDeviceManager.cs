using Microsoft.Extensions.Logging;
using NAudio.Wave;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Constants;

namespace VoicevoxRunCached.Services.Audio;

/// <summary>
/// 起動時のデバイス事前準備 (warm-up) を担当するクラス。
/// 最初のセグメントの取りこぼしを防ぐため、無音再生でドライバを起こしておく。
/// </summary>
public class AudioDeviceManager : IDisposable
{
    private readonly Task? _devicePreparationTask;
    private readonly AudioSettings _settings;
    private readonly ILogger _logger;
    private bool _disposed;

    public AudioDeviceManager(AudioSettings settings, ILogger logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (_settings.PrepareDevice)
        {
            _devicePreparationTask = Task.Run(async () =>
            {
                try
                {
                    await PrewarmAudioDeviceAsync(_settings.PreparationDurationMs);
                }
                catch
                {
                    _logger.LogDebug("デバイス事前準備でエラーが発生しましたが、継続します");
                }
            });
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        try
        {
            CleanupDevicePreparationTask();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AudioDeviceManagerの破棄中にエラーが発生しました");
        }

        _disposed = true;
    }

    public async Task EnsureDeviceReadyAsync()
    {
        if (_settings.PrepareDevice && _devicePreparationTask != null)
        {
            try
            { await _devicePreparationTask; }
            catch { _logger.LogDebug("デバイス準備が失敗しましたが、再生を継続します"); }
        }
    }

    private async Task PrewarmAudioDeviceAsync(int durationMs = 100)
    {
        try
        {
            var generateSilence = _settings.PreparationVolume <= 0;
            var silentWavData = AudioConversionUtility.CreateMinimalWavData(durationMs, generateSilence: generateSilence);

            using var audioStream = new MemoryStream(silentWavData);
            using var reader = new WaveFileReader(audioStream);
            using var wavePlayer = new WaveOutEvent();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(AudioConstants.DevicePreparationTimeoutSeconds));

            if (_settings.OutputDevice >= 0)
            {
                wavePlayer.DeviceNumber = _settings.OutputDevice;
            }

            wavePlayer.DesiredLatency = _settings.DesiredLatency;
            wavePlayer.NumberOfBuffers = _settings.NumberOfBuffers;

            wavePlayer.Volume = _settings.PreparationVolume <= 0
                ? 0.0f
                : (float)Math.Max(0.001, Math.Min(1.0, _settings.PreparationVolume));

            var tcs = new TaskCompletionSource<bool>();
            wavePlayer.PlaybackStopped += (_, _) => tcs.TrySetResult(true);

            wavePlayer.Init(reader);
            wavePlayer.Play();

            await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("デバイス事前準備がタイムアウトしました");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "デバイス事前準備に失敗しました");
        }
    }

    private void CleanupDevicePreparationTask()
    {
        if (_devicePreparationTask == null)
            return;

        try
        {
            if (!_devicePreparationTask.IsCompleted)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(AudioConstants.DevicePreparationCleanupTimeoutSeconds));
                _devicePreparationTask.Wait(cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("デバイス準備タスクのクリーンアップがタイムアウトしました");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "デバイス準備タスクのクリーンアップ中にエラーが発生しました");
        }
    }
}
