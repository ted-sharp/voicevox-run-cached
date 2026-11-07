using Microsoft.Extensions.Logging;
using NAudio.Wave;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Constants;

namespace VoicevoxRunCached.Services.Audio;

/// <summary>
/// 再生前のデバイス事前準備 (warm-up) を担当するクラス。
/// 最初のセグメントの取りこぼしを防ぐため、無音再生でドライバを起こしておく。
/// </summary>
/// <remarks>
/// warm-up は <see cref="StartPreparation"/> で始める (コンストラクタでは始めない)。
/// このクラスは CommandRouter の依存として全コマンドで生成されるため、コンストラクタで始めると
/// <c>speakers</c> / <c>devices</c> / <c>--clear</c> / <c>--no-play</c> でもウォームアップ音が鳴っていた。
/// </remarks>
public class AudioDeviceManager : IDisposable
{
    private readonly AudioSettings _settings;
    private readonly ILogger _logger;
    private Task? _devicePreparationTask;
    private bool _disposed;

    public AudioDeviceManager(AudioSettings settings, ILogger logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// <c>Audio.PrepareDevice</c> が有効なら warm-up をバックグラウンドで始める。2 回目以降は何もしない。
    /// </summary>
    /// <remarks>
    /// 再生することが決まった時点で呼び、キャッシュ参照やエンジン起動と並行して温めておく
    /// (完了は <see cref="EnsureDeviceReadyAsync"/> で待つ)。
    /// </remarks>
    public void StartPreparation()
    {
        if (!_settings.PrepareDevice || _devicePreparationTask != null)
        {
            return;
        }

        _logger.LogDebug("デバイス事前準備を開始します");
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

    /// <summary>
    /// warm-up の完了を待つ。まだ始めていなければここで始める。
    /// </summary>
    public async Task EnsureDeviceReadyAsync()
    {
        StartPreparation();

        if (_devicePreparationTask != null)
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
            using var wavePlayer = new WaveOut();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(AudioConstants.DevicePreparationTimeoutSeconds));

            if (_settings.OutputDevice >= 0)
            {
                wavePlayer.DeviceNumber = _settings.OutputDevice;
            }

            wavePlayer.BufferMilliseconds = WavePlayerManager.GetBufferMilliseconds(_settings);
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
