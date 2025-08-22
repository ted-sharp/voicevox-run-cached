using NAudio.Wave;
using VoicevoxRunCached.Configuration;
using Serilog;

namespace VoicevoxRunCached.Services.Audio;

/// <summary>
/// WavePlayerの作成・管理を行うクラス
/// </summary>
public class WavePlayerManager : IDisposable
{
    private readonly AudioSettings _settings;
    private IWavePlayer? _wavePlayer;
    private bool _disposed;

    public WavePlayerManager(AudioSettings settings)
    {
        this._settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// 基本的なWavePlayerを作成します
    /// </summary>
    /// <returns>作成されたWavePlayer</returns>
    public IWavePlayer CreateWavePlayer()
    {
        if (this._disposed)
            throw new ObjectDisposedException(nameof(WavePlayerManager));

        var waveOut = new WaveOutEvent
        {
            DesiredLatency = 100,
            NumberOfBuffers = 3
        };

        if (this._settings.OutputDevice >= 0)
        {
            waveOut.DeviceNumber = this._settings.OutputDevice;
        }

        // ボリューム設定
        waveOut.Volume = (float)Math.Max(0.0, Math.Min(1.0, this._settings.Volume));

        Log.Debug("WavePlayer を作成しました - デバイス: {Device}, ボリューム: {Volume}",
            this._settings.OutputDevice, this._settings.Volume);

        return waveOut;
    }

    /// <summary>
    /// 共有WavePlayerインスタンスを取得または作成します
    /// </summary>
    /// <returns>共有WavePlayerインスタンス</returns>
    public IWavePlayer GetOrCreateSharedWavePlayer()
    {
        if (this._disposed)
            throw new ObjectDisposedException(nameof(WavePlayerManager));

        if (this._wavePlayer == null)
        {
            this._wavePlayer = this.CreateWavePlayer();
            Log.Debug("共有WavePlayerインスタンスを作成しました");
        }

        return this._wavePlayer;
    }

    /// <summary>
    /// 共有WavePlayerインスタンスを設定します
    /// </summary>
    /// <param name="wavePlayer">設定するWavePlayer</param>
    public void SetSharedWavePlayer(IWavePlayer wavePlayer)
    {
        if (this._disposed)
            throw new ObjectDisposedException(nameof(WavePlayerManager));

        // 既存のインスタンスを破棄
        this._wavePlayer?.Dispose();

        this._wavePlayer = wavePlayer ?? throw new ArgumentNullException(nameof(wavePlayer));
        Log.Debug("共有WavePlayerインスタンスが設定されました");
    }

    /// <summary>
    /// 現在の共有WavePlayerインスタンスを取得します
    /// </summary>
    /// <returns>現在の共有WavePlayerインスタンス（設定されていない場合はnull）</returns>
    public IWavePlayer? GetCurrentSharedWavePlayer() => this._wavePlayer;

    /// <summary>
    /// 共有WavePlayerインスタンスが存在するかどうかを確認します
    /// </summary>
    /// <returns>存在する場合true</returns>
    public bool HasSharedWavePlayer() => this._wavePlayer != null;

    /// <summary>
    /// 共有WavePlayerインスタンスを停止します
    /// </summary>
    public void StopSharedWavePlayer()
    {
        try
        {
            if (this._wavePlayer != null)
            {
                this._wavePlayer.Stop();
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
            if (this._wavePlayer != null)
            {
                this._wavePlayer.Dispose();
                this._wavePlayer = null;
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
            CurrentDevice = this._settings.OutputDevice,
            CurrentDeviceName = "Default",
            Volume = this._settings.Volume,
            DesiredLatency = 100,
            NumberOfBuffers = 3
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
                Volume = this._settings.Volume,
                DesiredLatency = 100,
                NumberOfBuffers = 3
            }
        };
    }

    public void Dispose()
    {
        if (!this._disposed)
        {
            try
            {
                this.DisposeSharedWavePlayer();
                this._disposed = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "WavePlayerManagerの破棄中にエラーが発生しました");
            }
            finally
            {
                GC.SuppressFinalize(this);
            }
        }
    }
}

/// <summary>
/// 音声デバイス情報
/// </summary>
public class AudioDeviceInfo
{
    public int TotalDevices { get; set; }
    public int CurrentDevice { get; set; }
    public string CurrentDeviceName { get; set; } = string.Empty;
    public double Volume { get; set; }
    public int DesiredLatency { get; set; }
    public int NumberOfBuffers { get; set; }
}
