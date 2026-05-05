using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Constants;

namespace VoicevoxRunCached.Services;

/// <summary>
/// 音声デバイスの作成・管理・事前準備を行うクラス
/// </summary>
public class AudioDeviceManager : IDisposable
{
    private readonly Task? _devicePreparationTask;
    private readonly AudioSettings _settings;
    private readonly ILogger _logger;
    private bool _disposed;
    private MMDevice? _wasapiDevice;

    public AudioDeviceManager(AudioSettings settings, ILogger logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        MediaFoundationManager.EnsureInitialized();

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
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 設定に基づいて適切な WavePlayer を作成します
    /// </summary>
    public IWavePlayer CreateWavePlayer()
    {
        if (!String.IsNullOrWhiteSpace(_settings.OutputDeviceId))
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                _wasapiDevice = enumerator.GetDevice(_settings.OutputDeviceId);
                return new WasapiOut(_wasapiDevice, AudioClientShareMode.Shared, false, 100);
            }
            catch
            {
                _logger.LogDebug("WASAPIデバイスの作成に失敗しました。WaveOutEventにフォールバックします");
            }
        }

        var waveOut = new WaveOutEvent
        {
            DesiredLatency = 100,
            NumberOfBuffers = 3
        };

        if (_settings.OutputDevice >= 0)
        {
            waveOut.DeviceNumber = _settings.OutputDevice;
        }

        return waveOut;
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

            wavePlayer.DesiredLatency = 100;
            wavePlayer.NumberOfBuffers = 3;

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

    public async Task EnsureDeviceReadyAsync()
    {
        if (_settings.PrepareDevice && _devicePreparationTask != null)
        {
            try
            { await _devicePreparationTask; }
            catch { _logger.LogDebug("デバイス準備が失敗しましたが、再生を継続します"); }
        }
    }

    public static List<string> GetAvailableDevices()
    {
        try
        {
            return ["-1: Default Device"];
        }
        catch
        {
            return [];
        }
    }

    ~AudioDeviceManager()
    {
        Dispose(false);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            try
            {
                CleanupDevicePreparationTask();
                CleanupWasapiDevice();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AudioDeviceManagerの破棄中にエラーが発生しました");
            }
        }

        _disposed = true;
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

    private void CleanupWasapiDevice()
    {
        if (_wasapiDevice == null)
            return;

        try
        {
            _wasapiDevice.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WASAPIデバイスの破棄中にエラーが発生しました");
        }
        finally
        {
            _wasapiDevice = null;
        }
    }
}
