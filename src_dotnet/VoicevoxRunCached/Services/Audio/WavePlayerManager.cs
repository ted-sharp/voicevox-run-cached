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
    private int _wavePlayerDevice;

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

    /// <summary>
    /// <paramref name="device"/> で鳴らす共有 WavePlayer を返す。前回と違うデバイスなら作り直す
    /// (<c>devices --speak</c> は 1 回の実行でデバイスを順に替えて鳴らす)。
    /// </summary>
    /// <param name="device">WaveOut の index (-1 は OS 既定)</param>
    public IWavePlayer GetOrCreateSharedWavePlayer(int device)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WavePlayerManager));

        if (_wavePlayer != null && _wavePlayerDevice != device)
        {
            _wavePlayer.Dispose();
            _wavePlayer = null;
        }
        _wavePlayerDevice = device;
        _wavePlayer ??= CreateWavePlayer(device);
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

    /// <summary>
    /// 設定の DesiredLatency (全バッファ合計の遅延) を <see cref="WaveOut.BufferMilliseconds"/> (バッファ 1 本の長さ) に換算する。
    /// </summary>
    /// <remarks>
    /// NAudio 3 で <c>WaveOutEvent.DesiredLatency</c> が無くなったため。式は NAudio 2 の <c>WaveOutEvent.Init</c> と同じ (切り上げ)。
    /// </remarks>
    public static int GetBufferMilliseconds(AudioSettings settings)
    {
        return (settings.DesiredLatency + settings.NumberOfBuffers - 1) / settings.NumberOfBuffers;
    }

    private IWavePlayer CreateWavePlayer(int device)
    {
        var waveOut = new WaveOut
        {
            BufferMilliseconds = GetBufferMilliseconds(_settings),
            NumberOfBuffers = _settings.NumberOfBuffers
        };

        if (device >= 0)
        {
            waveOut.DeviceNumber = device;
        }

        // 範囲 (0.0-1.0) は AudioSettingsValidator で保証済み
        waveOut.Volume = (float)_settings.Volume;

        _logger.LogDebug("WavePlayer を作成しました - デバイス: {Device}, ボリューム: {Volume}",
            device, _settings.Volume);

        return waveOut;
    }
}
