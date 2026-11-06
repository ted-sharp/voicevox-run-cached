using Microsoft.Extensions.Logging;
using NAudio.Wave;
using VoicevoxRunCached.Configuration;

namespace VoicevoxRunCached.Services.Audio;

/// <summary>
/// セッション内で使い回す共有 <see cref="IWavePlayer"/> のライフサイクルを管理する。
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
        if (_disposed)
        {
            return;
        }
        try
        {
            _wavePlayer?.Dispose();
            _wavePlayer = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "共有 WavePlayer の破棄中にエラーが発生しました");
        }
        _disposed = true;
    }

    public IWavePlayer GetOrCreateSharedWavePlayer()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WavePlayerManager));

        _wavePlayer ??= CreateWavePlayer();
        return _wavePlayer;
    }

    public void StopSharedWavePlayer()
    {
        try
        {
            _wavePlayer?.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "共有 WavePlayer の停止中にエラーが発生しました");
        }
    }

    private IWavePlayer CreateWavePlayer()
    {
        var waveOut = new WaveOutEvent
        {
            DesiredLatency = _settings.DesiredLatency,
            NumberOfBuffers = _settings.NumberOfBuffers
        };

        if (_settings.OutputDevice >= 0)
        {
            waveOut.DeviceNumber = _settings.OutputDevice;
        }

        // 範囲 (0.0-1.0) は AudioSettingsValidator で保証済み
        waveOut.Volume = (float)_settings.Volume;

        _logger.LogDebug("WavePlayer を作成しました - デバイス: {Device}, ボリューム: {Volume}",
            _settings.OutputDevice, _settings.Volume);

        return waveOut;
    }
}
