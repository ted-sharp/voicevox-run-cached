using NAudio.Wave;
using VoicevoxRunCached.Configuration;

namespace VoicevoxRunCached.Services;

public class AudioPlayer : IDisposable
{
    private readonly AudioSettings _settings;
    private IWavePlayer? _wavePlayer;
    private bool _disposed;

    public AudioPlayer(AudioSettings settings)
    {
        _settings = settings;
    }

    public async Task PlayAudioAsync(byte[] audioData)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AudioPlayer));

        try
        {
            StopAudio();

            using var audioStream = new MemoryStream(audioData);
            using var waveFileReader = new WaveFileReader(audioStream);
            
            _wavePlayer = new WaveOutEvent();
            
            if (_settings.OutputDevice >= 0)
            {
                ((WaveOutEvent)_wavePlayer).DeviceNumber = _settings.OutputDevice;
            }

            _wavePlayer.Volume = (float)Math.Max(0.0, Math.Min(1.0, _settings.Volume));
            
            var tcs = new TaskCompletionSource<bool>();
            
            _wavePlayer.PlaybackStopped += (sender, e) =>
            {
                if (e.Exception != null)
                {
                    tcs.TrySetException(e.Exception);
                }
                else
                {
                    tcs.TrySetResult(true);
                }
            };

            _wavePlayer.Init(waveFileReader);
            _wavePlayer.Play();

            await tcs.Task;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to play audio: {ex.Message}", ex);
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
            if (_wavePlayer != null)
            {
                _wavePlayer.Stop();
                _wavePlayer.Dispose();
                _wavePlayer = null;
            }
        }
        catch
        {
        }
    }

    public static List<string> GetAvailableDevices()
    {
        var devices = new List<string>();
        
        try
        {
            // WaveOutEvent doesn't have static DeviceCount/GetCapabilities methods
            // We'll return a simple placeholder for now
            devices.Add("0: Default Device");
        }
        catch
        {
        }
        
        return devices;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            StopAudio();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}