using Microsoft.Extensions.Logging;
using NAudio.Wave;
using VoicevoxRunCached.Configuration;

namespace VoicevoxRunCached.Services.Audio;

/// <summary>
/// WavePlayer の作成・管理を行うクラス
/// </summary>
public class WavePlayerManager : IDisposable
{
    private readonly AudioSettings _settings;
    private readonly ILogger _logger;
    private bool _disposed;
    private IWavePlayer? _wavePlayer;

    public WavePlayerManager(AudioSettings settings, ILogger logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
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
            { DisposeSharedWavePlayer(); }
            catch (Exception ex) { _logger.LogError(ex, "WavePlayerManagerの破棄中にエラーが発生しました"); }
        }
        _disposed = true;
    }

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

        waveOut.Volume = (float)Math.Max(0.0, Math.Min(1.0, _settings.Volume));

        _logger.LogDebug("WavePlayer を作成しました - デバイス: {Device}, ボリューム: {Volume}",
            _settings.OutputDevice, _settings.Volume);

        return waveOut;
    }

    public IWavePlayer GetOrCreateSharedWavePlayer()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WavePlayerManager));

        if (_wavePlayer == null)
        {
            _wavePlayer = CreateWavePlayer();
            _logger.LogDebug("共有WavePlayerインスタンスを作成しました");
        }

        return _wavePlayer;
    }

    public void SetSharedWavePlayer(IWavePlayer wavePlayer)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WavePlayerManager));

        _wavePlayer?.Dispose();
        _wavePlayer = wavePlayer ?? throw new ArgumentNullException(nameof(wavePlayer));
        _logger.LogDebug("共有WavePlayerインスタンスが設定されました");
    }

    public IWavePlayer? GetCurrentSharedWavePlayer() => _wavePlayer;

    public bool HasSharedWavePlayer() => _wavePlayer != null;

    public void StopSharedWavePlayer()
    {
        try
        {
            if (_wavePlayer != null)
            {
                _wavePlayer.Stop();
                _logger.LogDebug("共有WavePlayerインスタンスを停止しました");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "共有WavePlayerインスタンスの停止中にエラーが発生しました");
        }
    }

    public void DisposeSharedWavePlayer()
    {
        try
        {
            if (_wavePlayer != null)
            {
                _wavePlayer.Dispose();
                _wavePlayer = null;
                _logger.LogDebug("共有WavePlayerインスタンスを破棄しました");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "共有WavePlayerインスタンスの破棄中にエラーが発生しました");
        }
    }

    public AudioDeviceInfo GetAudioDeviceInfo() => new()
    {
        TotalDevices = 0,
        CurrentDevice = _settings.OutputDevice,
        CurrentDeviceName = "Default",
        Volume = _settings.Volume,
        DesiredLatency = _settings.DesiredLatency,
        NumberOfBuffers = _settings.NumberOfBuffers
    };

    public List<AudioDeviceInfo> GetAvailableAudioDevices() => new()
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
