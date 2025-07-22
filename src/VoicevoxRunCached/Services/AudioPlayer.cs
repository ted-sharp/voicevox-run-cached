using NAudio.Wave;
using NAudio.MediaFoundation;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;

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

    public async Task PlayAudioStreamingAsync(byte[] audioData, Func<byte[], Task>? cacheCallback = null)
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

            // Optimized buffering settings for minimal latency with stability
            ((WaveOutEvent)_wavePlayer).DesiredLatency = 80; // 80ms buffer
            ((WaveOutEvent)_wavePlayer).NumberOfBuffers = 2;  // Use 2 buffers

            _wavePlayer.Volume = (float)Math.Max(0.0, Math.Min(1.0, _settings.Volume));
            
            var tcs = new TaskCompletionSource<bool>();
            
            // Start cache saving in parallel if callback provided
            Task? cacheTask = null;
            if (cacheCallback != null)
            {
                cacheTask = Task.Run(async () =>
                {
                    try
                    {
                        await cacheCallback(audioData);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Warning: Failed to cache audio: {ex.Message}");
                    }
                });
            }
            
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
            
            // Minimal delay to ensure proper audio initialization
            await Task.Delay(20);
            
            _wavePlayer.Play();

            await tcs.Task;
            
            // Ensure all buffered audio is played before stopping
            await Task.Delay(150); // Wait for buffer to flush
            
            // Wait for cache to complete if it's running
            if (cacheTask != null)
            {
                await cacheTask;
            }
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

    public async Task PlayAudioSequentiallyAsync(List<byte[]> audioSegments)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AudioPlayer));

        try
        {
            // Initialize single WavePlayer instance for all segments
            _wavePlayer = new WaveOutEvent();
            
            if (_settings.OutputDevice >= 0)
            {
                ((WaveOutEvent)_wavePlayer).DeviceNumber = _settings.OutputDevice;
            }

            // Optimized buffering settings for minimal latency with stability
            ((WaveOutEvent)_wavePlayer).DesiredLatency = 60; // Reduced to 60ms for faster transitions
            ((WaveOutEvent)_wavePlayer).NumberOfBuffers = 3;  // Increased to 3 buffers for seamless transitions

            _wavePlayer.Volume = (float)Math.Max(0.0, Math.Min(1.0, _settings.Volume));

            foreach (var segment in audioSegments)
            {
                if (segment.Length == 0) continue;
                
                await PlaySegmentAsync(segment);
            }
        }
        finally
        {
            StopAudio();
        }
    }

    public async Task PlayAudioSequentiallyWithGenerationAsync(List<TextSegment> segments, Task? generationTask)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AudioPlayer));

        try
        {
            // Initialize single WavePlayer instance for all segments
            _wavePlayer = new WaveOutEvent();
            
            if (_settings.OutputDevice >= 0)
            {
                ((WaveOutEvent)_wavePlayer).DeviceNumber = _settings.OutputDevice;
            }

            // Optimized buffering settings for minimal latency with stability
            ((WaveOutEvent)_wavePlayer).DesiredLatency = 60; // Reduced to 60ms for faster transitions
            ((WaveOutEvent)_wavePlayer).NumberOfBuffers = 3;  // Increased to 3 buffers for seamless transitions

            _wavePlayer.Volume = (float)Math.Max(0.0, Math.Min(1.0, _settings.Volume));

            for (int i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];
                
                // If segment is not cached, wait for generation to complete up to this point
                if (!segment.IsCached || segment.AudioData == null)
                {
                    Console.WriteLine($"Waiting for segment {i + 1} to be generated...");
                    
                    // Wait until this segment is ready or generation task completes
                    while (!segment.IsCached || segment.AudioData == null)
                    {
                        if (generationTask?.IsCompleted == true)
                        {
                            break; // Generation is done, no point in waiting further
                        }
                        await Task.Delay(50); // Check every 50ms
                    }
                    
                    if (segment.AudioData == null)
                    {
                        Console.WriteLine($"Warning: Segment {i + 1} could not be generated, skipping...");
                        continue;
                    }
                }
                
                await PlaySegmentAsync(segment.AudioData);
            }

            // Ensure background generation is complete
            if (generationTask != null)
            {
                await generationTask;
            }
        }
        finally
        {
            StopAudio();
        }
    }

    private async Task PlaySegmentAsync(byte[] audioData)
    {
        try
        {
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
            
            // Minimal delay for initialization - reduced for faster transitions
            await Task.Delay(10);
            
            _wavePlayer.Play();

            await tcs.Task;
            
            // Ensure complete audio playback - increased delay for proper segment completion
            await Task.Delay(120);
            
            // Stop but don't dispose the WavePlayer - reuse for next segment
            _wavePlayer.Stop();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to play audio segment: {ex.Message}", ex);
        }
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

            // Optimized buffering settings for minimal latency with stability
            ((WaveOutEvent)_wavePlayer).DesiredLatency = 80; // 80ms buffer
            ((WaveOutEvent)_wavePlayer).NumberOfBuffers = 2;  // Use 2 buffers

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
            
            // Minimal delay to ensure proper audio initialization
            await Task.Delay(20);
            
            _wavePlayer.Play();

            await tcs.Task;
            
            // Ensure all buffered audio is played before stopping
            await Task.Delay(150); // Wait for buffer to flush
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