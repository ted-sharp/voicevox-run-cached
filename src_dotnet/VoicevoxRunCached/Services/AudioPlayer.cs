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
        
        // Pre-warm audio device to avoid initialization delay on first playback
        _ = Task.Run(async () =>
        {
            try
            {
                await PrewarmAudioDeviceAsync();
            }
            catch
            {
                // Ignore pre-warming errors - not critical
            }
        });
    }

    private async Task PrewarmAudioDeviceAsync()
    {
        try
        {
            // Create a very short silent audio to initialize the device
            var silentWavData = CreateSilentWavData(100); // 100ms silence
            
            using var audioStream = new MemoryStream(silentWavData);
            using var reader = new WaveFileReader(audioStream);
            using var wavePlayer = new WaveOutEvent();
            
            if (_settings.OutputDevice >= 0)
            {
                wavePlayer.DeviceNumber = _settings.OutputDevice;
            }
            
            // Use same buffer settings as main playback
            wavePlayer.DesiredLatency = 100;
            wavePlayer.NumberOfBuffers = 3;
            wavePlayer.Volume = 0.0f; // Silent pre-warming
            
            var tcs = new TaskCompletionSource<bool>();
            
            wavePlayer.PlaybackStopped += (sender, e) =>
            {
                tcs.TrySetResult(true);
            };
            
            wavePlayer.Init(reader);
            wavePlayer.Play();
            
            // Wait for pre-warming to complete or timeout after 2 seconds
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Pre-warming failed, but this is not critical
        }
    }

    private byte[] CreateSilentWavData(int durationMs)
    {
        // Create minimal WAV file with silence using efficient Span operations
        const int sampleRate = 22050;
        const int channels = 1;
        const int bitsPerSample = 16;
        
        var samplesCount = (sampleRate * durationMs) / 1000;
        var dataSize = samplesCount * channels * (bitsPerSample / 8);
        var fileSize = 44 + dataSize - 8;
        
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        
        // WAV header using ReadOnlySpan for string constants
        ReadOnlySpan<char> riffChars = "RIFF";
        ReadOnlySpan<char> waveChars = "WAVE";
        ReadOnlySpan<char> fmtChars = "fmt ";
        ReadOnlySpan<char> dataChars = "data";
        
        writer.Write(riffChars.ToArray());
        writer.Write(fileSize);
        writer.Write(waveChars.ToArray());
        writer.Write(fmtChars.ToArray());
        writer.Write(16); // PCM format chunk size
        writer.Write((short)1); // PCM format
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * (bitsPerSample / 8)); // Byte rate
        writer.Write((short)(channels * (bitsPerSample / 8))); // Block align
        writer.Write((short)bitsPerSample);
        writer.Write(dataChars.ToArray());
        writer.Write(dataSize);
        
        // Silent audio data (all zeros) - use zero-filled span
        Span<short> silentSamples = stackalloc short[samplesCount];
        silentSamples.Clear(); // Initialize to zeros
        
        // Write samples efficiently
        foreach (var sample in silentSamples)
        {
            writer.Write(sample);
        }
        
        return stream.ToArray();
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
            
            // Try to detect if it's MP3 or WAV by reading the header using Memory for async operations
            audioStream.Position = 0;
            var headerBuffer = new byte[12];
            var bytesRead = await audioStream.ReadAsync(headerBuffer, 0, 12);
            ReadOnlySpan<byte> headerSpan = headerBuffer;
            ref readonly var headerRef = ref headerSpan[0];
            audioStream.Position = 0;
            
            // Check for WAV header (RIFF....WAVE) using ref for efficient access
            bool isWav = bytesRead >= 12 && 
                         headerRef == 'R' && headerSpan[1] == 'I' && headerSpan[2] == 'F' && headerSpan[3] == 'F' &&
                         headerSpan[8] == 'W' && headerSpan[9] == 'A' && headerSpan[10] == 'V' && headerSpan[11] == 'E';
            
            // Check for MP3 header (starts with 0xFF) using ref for efficient access
            bool isMp3 = bytesRead >= 2 && headerRef == 0xFF && (headerSpan[1] & 0xE0) == 0xE0;
            
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

            // Optimized buffering settings for stability - slightly higher latency for reliability
            ((WaveOutEvent)_wavePlayer).DesiredLatency = 100; // 100ms for stable initialization
            ((WaveOutEvent)_wavePlayer).NumberOfBuffers = 3;   // 3 buffers for seamless transitions

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

            // Optimized buffering settings for stability - slightly higher latency for reliability
            ((WaveOutEvent)_wavePlayer).DesiredLatency = 100; // 100ms for stable initialization
            ((WaveOutEvent)_wavePlayer).NumberOfBuffers = 3;   // 3 buffers for seamless transitions

            _wavePlayer.Volume = (float)Math.Max(0.0, Math.Min(1.0, _settings.Volume));

            bool isFirstSegment = true;
            
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
                
                await PlaySegmentAsync(segment.AudioData, isFirstSegment);
                isFirstSegment = false;
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

    private async Task PlaySegmentAsync(byte[] audioData, bool isFirstSegment = false)
    {
        try
        {
            using var audioStream = new MemoryStream(audioData);
            WaveStream reader;
            
            // Try to detect if it's MP3 or WAV by reading the header using Memory for async operations
            audioStream.Position = 0;
            var headerBuffer = new byte[12];
            var bytesRead = await audioStream.ReadAsync(headerBuffer, 0, 12);
            ReadOnlySpan<byte> headerSpan = headerBuffer;
            ref readonly var headerRef = ref headerSpan[0];
            audioStream.Position = 0;
            
            // Check for WAV header (RIFF....WAVE) using ref for efficient access
            bool isWav = bytesRead >= 12 && 
                         headerRef == 'R' && headerSpan[1] == 'I' && headerSpan[2] == 'F' && headerSpan[3] == 'F' &&
                         headerSpan[8] == 'W' && headerSpan[9] == 'A' && headerSpan[10] == 'V' && headerSpan[11] == 'E';
            
            // Check for MP3 header (starts with 0xFF) using ref for efficient access
            bool isMp3 = bytesRead >= 2 && headerRef == 0xFF && (headerSpan[1] & 0xE0) == 0xE0;
            
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
            
            // First segment needs longer initialization for audio device setup
            if (isFirstSegment)
            {
                // Extended delay for first segment to ensure proper audio device initialization
                // Wait for pre-warming to complete if still in progress
                await Task.Delay(200); // 200ms for device initialization and stability
            }
            else
            {
                // Minimal delay for subsequent segments
                await Task.Delay(10);
            }
            
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
            
            // Try to detect if it's MP3 or WAV by reading the header using Memory for async operations
            audioStream.Position = 0;
            var headerBuffer = new byte[12];
            var bytesRead = await audioStream.ReadAsync(headerBuffer, 0, 12);
            ReadOnlySpan<byte> headerSpan = headerBuffer;
            ref readonly var headerRef = ref headerSpan[0];
            audioStream.Position = 0;
            
            // Check for WAV header (RIFF....WAVE) using ref for efficient access
            bool isWav = bytesRead >= 12 && 
                         headerRef == 'R' && headerSpan[1] == 'I' && headerSpan[2] == 'F' && headerSpan[3] == 'F' &&
                         headerSpan[8] == 'W' && headerSpan[9] == 'A' && headerSpan[10] == 'V' && headerSpan[11] == 'E';
            
            // Check for MP3 header (starts with 0xFF) using ref for efficient access
            bool isMp3 = bytesRead >= 2 && headerRef == 0xFF && (headerSpan[1] & 0xE0) == 0xE0;
            
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