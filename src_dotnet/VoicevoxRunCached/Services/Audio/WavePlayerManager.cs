using NAudio.Wave;
using Serilog;
using VoicevoxRunCached.Configuration;

namespace VoicevoxRunCached.Services.Audio;

/// <summary>
/// WavePlayerの作成・管理を行うクラス
/// </summary>
public class WavePlayerManager : IDisposable
{
    private readonly AudioSettings _settings;
    private bool _disposed;
    private IWavePlayer? _wavePlayer;

    public WavePlayerManager(AudioSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                try
                {
                    DisposeSharedWavePlayer();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "WavePlayerManagerの破棄中にエラーが発生しました");
                }
            }
            _disposed = true;
        }
    }

    /// <summary>
    /// 基本的なWavePlayerを作成します
    /// </summary>
    /// <returns>作成されたWavePlayer</returns>
    public IWavePlayer CreateWavePlayer()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WavePlayerManager));

        var waveOut = new WaveOutEvent
        {
            DesiredLatency = _settings.DesiredLatency,
            NumberOfBuffers = _settings.NumberOfBuffers
        };

        if (_settings.OutputDevice >= 0)
        {
            waveOut.DeviceNumber = _settings.OutputDevice;
        }

        // ボリューム設定
        waveOut.Volume = (float)Math.Max(0.0, Math.Min(1.0, _settings.Volume));

        Log.Debug("WavePlayer を作成しました - デバイス: {Device}, ボリューム: {Volume}",
            _settings.OutputDevice, _settings.Volume);

        return waveOut;
    }

    /// <summary>
    /// 共有WavePlayerインスタンスを取得または作成します
    /// </summary>
    /// <returns>共有WavePlayerインスタンス</returns>
    public IWavePlayer GetOrCreateSharedWavePlayer()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WavePlayerManager));

        if (_wavePlayer == null)
        {
            _wavePlayer = CreateWavePlayer();
            Log.Debug("共有WavePlayerインスタンスを作成しました");
        }

        return _wavePlayer;
    }

    /// <summary>
    /// 共有WavePlayerインスタンスを設定します
    /// </summary>
    /// <param name="wavePlayer">設定するWavePlayer</param>
    public void SetSharedWavePlayer(IWavePlayer wavePlayer)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WavePlayerManager));

        // 既存のインスタンスを破棄
        _wavePlayer?.Dispose();

        _wavePlayer = wavePlayer ?? throw new ArgumentNullException(nameof(wavePlayer));
        Log.Debug("共有WavePlayerインスタンスが設定されました");
    }

    /// <summary>
    /// 現在の共有WavePlayerインスタンスを取得します
    /// </summary>
    /// <returns>現在の共有WavePlayerインスタンス（設定されていない場合はnull）</returns>
    public IWavePlayer? GetCurrentSharedWavePlayer() => _wavePlayer;

    /// <summary>
    /// 共有WavePlayerインスタンスが存在するかどうかを確認します
    /// </summary>
    /// <returns>存在する場合true</returns>
    public bool HasSharedWavePlayer() => _wavePlayer != null;

    /// <summary>
    /// 共有WavePlayerインスタンスを停止します
    /// </summary>
    public void StopSharedWavePlayer()
    {
        try
        {
            if (_wavePlayer != null)
            {
                _wavePlayer.Stop();
                Log.Debug("共有WavePlayerインスタンスを停止しました");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "共有WavePlayerインスタンスの停止中にエラーが発生しました");
        }
    }

    /// <summary>
    /// 共有WavePlayerインスタンスを破棄します
    /// </summary>
    public void DisposeSharedWavePlayer()
    {
        try
        {
            if (_wavePlayer != null)
            {
                _wavePlayer.Dispose();
                _wavePlayer = null;
                Log.Debug("共有WavePlayerインスタンスを破棄しました");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "共有WavePlayerインスタンスの破棄中にエラーが発生しました");
        }
    }

    /// <summary>
    /// 音声デバイスの情報を取得します
    /// </summary>
    /// <returns>音声デバイス情報</returns>
    public AudioDeviceInfo GetAudioDeviceInfo()
    {
        return new AudioDeviceInfo
        {
            TotalDevices = 0,
            CurrentDevice = _settings.OutputDevice,
            CurrentDeviceName = "Default",
            Volume = _settings.Volume,
            DesiredLatency = _settings.DesiredLatency,
            NumberOfBuffers = _settings.NumberOfBuffers
        };
    }

    /// <summary>
    /// 利用可能な音声デバイスの一覧を取得します
    /// </summary>
    /// <returns>音声デバイスの一覧</returns>
    public List<AudioDeviceInfo> GetAvailableAudioDevices()
    {
        return new List<AudioDeviceInfo>
        {
            new AudioDeviceInfo
            {
                TotalDevices = 1,
                CurrentDevice = 0,
                CurrentDeviceName = "Default Audio Device",
                Volume = _settings.Volume,
                DesiredLatency = _settings.DesiredLatency,
                NumberOfBuffers = _settings.NumberOfBuffers
            }
        };
    }
}

/// <summary>
/// 音声デバイス情報
/// </summary>
public class AudioDeviceInfo
{
    public int TotalDevices { get; set; }
    public int CurrentDevice { get; set; }
    public string CurrentDeviceName { get; set; } = String.Empty;
    public double Volume { get; set; }
    public int DesiredLatency { get; set; }
    public int NumberOfBuffers { get; set; }
}
