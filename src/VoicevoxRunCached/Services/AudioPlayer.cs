using NAudio.Wave;
using NAudio.MediaFoundation;
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
        MediaFoundationApi.Startup();
    }

    public async Task PlayAudioAsync(byte[] audioData)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AudioPlayer));

        try
        {
            StopAudio();

            using var audioStream = new MemoryStream(audioData);
            WaveStream reader;
            
            // Try to detect if it's MP3 or WAV by reading the header
            audioStream.Position = 0;
            var header = new byte[12];
            var bytesRead = await audioStream.ReadAsync(header, 0, 12);
            audioStream.Position = 0;
            
            // Check for WAV header (RIFF....WAVE)
            bool isWav = bytesRead >= 12 && 
                         header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F' &&
                         header[8] == 'W' && header[9] == 'A' && header[10] == 'V' && header[11] == 'E';
            
            // Check for MP3 header (starts with 0xFF)
            bool isMp3 = bytesRead >= 2 && header[0] == 0xFF && (header[1] & 0xE0) == 0xE0;
            
            if (isWav)
            {
                reader = new WaveFileReader(audioStream);
            }
            else if (isMp3)
            {
                reader = new Mp3FileReader(audioStream);
            }
            else
            {
                // Try MP3 first since we're primarily caching MP3 files now
                try
                {
                    audioStream.Position = 0;
                    reader = new Mp3FileReader(audioStream);
                }
                catch
                {
                    // Fall back to WAV if MP3 fails
                    audioStream.Position = 0;
                    reader = new WaveFileReader(audioStream);
                }
            }
            
            _wavePlayer = new WaveOutEvent();
            
            if (_settings.OutputDevice >= 0)
            {
                ((WaveOutEvent)_wavePlayer).DeviceNumber = _settings.OutputDevice;
            }

            _wavePlayer.Volume = (float)Math.Max(0.0, Math.Min(1.0, _settings.Volume));
            
            var tcs = new TaskCompletionSource<bool>();
            
            _wavePlayer.PlaybackStopped += (sender, e) =>
            {
                reader.Dispose();
                if (e.Exception != null)
                {
                    tcs.TrySetException(e.Exception);
                }
                else
                {
                    tcs.TrySetResult(true);
                }
            };

            _wavePlayer.Init(reader);
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
            MediaFoundationApi.Shutdown();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}