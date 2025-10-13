using NAudio.CoreAudioApi;
using NAudio.Wave;
using Serilog;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Constants;

namespace VoicevoxRunCached.Services;

/// <summary>
/// 音声デバイスの作成、管理、事前準備を行うクラス
/// </summary>
public class AudioDeviceManager : IDisposable
{
    private readonly Task? _devicePreparationTask;
    private readonly AudioSettings _settings;
    private bool _disposed;
    private MMDevice? _wasapiDevice;

    public AudioDeviceManager(AudioSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        MediaFoundationManager.EnsureInitialized();

        // 設定でデバイス事前準備が有効な場合、開始
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
                    // 事前準備のエラーは無視（クリティカルではない）
                    Log.Debug("デバイス事前準備でエラーが発生しましたが、継続します");
                }
            });
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            try
            {
                // デバイス準備タスクのクリーンアップ
                if (_devicePreparationTask != null)
                {
                    try
                    {
                        // 非同期タスクの適切な破棄
                        if (!_devicePreparationTask.IsCompleted)
                        {
                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(AudioConstants.DevicePreparationCleanupTimeoutSeconds));
                            _devicePreparationTask.Wait(cts.Token);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Log.Debug("デバイス準備タスクのクリーンアップがタイムアウトしました");
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "デバイス準備タスクのクリーンアップ中にエラーが発生しました");
                    }
                }

                // WASAPIデバイスのクリーンアップ
                if (_wasapiDevice != null)
                {
                    try
                    {
                        _wasapiDevice.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "WASAPIデバイスの破棄中にエラーが発生しました");
                    }
                    finally
                    {
                        _wasapiDevice = null;
                    }
                }

                _disposed = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "AudioDeviceManagerの破棄中にエラーが発生しました");
            }
            finally
            {
                GC.SuppressFinalize(this);
            }
        }
    }

    /// <summary>
    /// 設定に基づいて適切なWavePlayerを作成します
    /// </summary>
    /// <returns>作成されたWavePlayer</returns>
    public IWavePlayer CreateWavePlayer()
    {
        // WASAPIエンドポイントIDが指定されている場合は優先
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
                // WASAPIが失敗した場合はWaveOutEventにフォールバック
                Log.Debug("WASAPIデバイスの作成に失敗しました。WaveOutEventにフォールバックします");
            }
        }

        // デフォルトはWaveOutEvent
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

    /// <summary>
    /// 音声デバイスを事前に準備します
    /// </summary>
    /// <param name="durationMs">準備時間（ミリ秒）</param>
    /// <returns>準備完了を表すTask</returns>
    private async Task PrewarmAudioDeviceAsync(int durationMs = 100)
    {
        try
        {
            // 無音音声を作成してデバイスを初期化
            var generateSilence = _settings.PreparationVolume <= 0;
            var silentWavData = AudioConversionUtility.CreateMinimalWavData(durationMs, generateSilence: generateSilence);

            using var audioStream = new MemoryStream(silentWavData);
            using var reader = new WaveFileReader(audioStream);
            using var wavePlayer = new WaveOutEvent();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(AudioConstants.DevicePreparationTimeoutSeconds)); // デバイス準備のタイムアウト

            if (_settings.OutputDevice >= 0)
            {
                wavePlayer.DeviceNumber = _settings.OutputDevice;
            }

            // メイン再生と同じバッファ設定を使用
            wavePlayer.DesiredLatency = 100;
            wavePlayer.NumberOfBuffers = 3;

            // 効果的なデバイス準備のため、非常に低いが聞こえる音量を使用
            // 音量が0の場合は完全に無音
            if (_settings.PreparationVolume <= 0)
            {
                wavePlayer.Volume = 0.0f; // 完全に無音
            }
            else
            {
                wavePlayer.Volume = (float)Math.Max(0.001, Math.Min(1.0, _settings.PreparationVolume));
            }

            var tcs = new TaskCompletionSource<bool>();

            wavePlayer.PlaybackStopped += (_, _) =>
            {
                tcs.TrySetResult(true);
            };

            wavePlayer.Init(reader);
            wavePlayer.Play();

            // 事前準備の完了をタイムアウト付きで待機
            await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Log.Debug("デバイス事前準備がタイムアウトしました");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "デバイス事前準備に失敗しました");
        }
    }

    /// <summary>
    /// 再生開始前にデバイスの準備完了を確認します
    /// </summary>
    /// <returns>準備完了を表すTask</returns>
    public async Task EnsureDeviceReadyAsync()
    {
        if (_settings.PrepareDevice && _devicePreparationTask != null)
        {
            try
            {
                await _devicePreparationTask;
            }
            catch
            {
                // デバイス準備が失敗しても再生は継続
                Log.Debug("デバイス準備が失敗しましたが、再生を継続します");
            }
        }
    }

    /// <summary>
    /// 利用可能なデバイスの一覧を取得します
    /// </summary>
    /// <returns>デバイス一覧</returns>
    public static List<string> GetAvailableDevices()
    {
        try
        {
            // プラットフォーム固有の列挙問題を避けるため、シンプルなデフォルトデバイス一覧を保持
            return ["-1: Default Device"];
        }
        catch
        {
            return [];
        }
    }

    // ファイナライザー（安全性のため）
    ~AudioDeviceManager()
    {
        Dispose(false);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                // マネージドリソースの破棄
                Dispose();
            }
            else
            {
                // ファイナライザーが呼ばれた場合 - アンマネージドリソースのみ破棄
                try
                {
                    // アンマネージドリソースの破棄
                }
                catch
                {
                    // ファイナライザーでのエラーは無視
                }
            }
        }
    }
}
