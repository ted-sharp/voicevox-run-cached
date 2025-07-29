using NAudio.Wave;
using NAudio.MediaFoundation;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;

// C# 13 Using alias for commonly used types
using WavePlayer = NAudio.Wave.WaveOutEvent;
using AudioStream = System.IO.MemoryStream;

namespace VoicevoxRunCached.Services;

public class AudioPlayer : IDisposable
{
    private readonly AudioSettings _settings;
    private IWavePlayer? _wavePlayer;
    private bool _disposed;
    private Task? _devicePreparationTask;

    public AudioPlayer(AudioSettings settings)
    {
        // C# 13 nameof expression for type-safe parameter validation
        this._settings = settings ?? throw new ArgumentNullException(nameof(settings));
        MediaFoundationApi.Startup();

        // Start device preparation if enabled in settings
        if (this._settings.PrepareDevice)
        {
            this._devicePreparationTask = Task.Run(async () =>
            {
                try
                {
                    await this.PrewarmAudioDeviceAsync(this._settings.PreparationDurationMs);
                }
                catch
                {
                    // Ignore pre-warming errors - not critical
                }
            });
        }
    }

    private async Task PrewarmAudioDeviceAsync(int durationMs = 100)
    {
        try
        {
            // Create a silent audio to initialize the device with configurable duration
            var silentWavData = this.CreateSilentWavData(durationMs);

            using var audioStream = new MemoryStream(silentWavData);
            using var reader = new WaveFileReader(audioStream);
            using var wavePlayer = new WaveOutEvent();

            if (this._settings.OutputDevice >= 0)
            {
                wavePlayer.DeviceNumber = this._settings.OutputDevice;
            }

            // Use same buffer settings as main playback
            wavePlayer.DesiredLatency = 100;
            wavePlayer.NumberOfBuffers = 3;
            // Use very low but audible volume for effective device warming
            wavePlayer.Volume = (float)Math.Max(0.001, Math.Min(1.0, this._settings.PreparationVolume));

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
        // Create minimal WAV file with very low tone for device warming
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

        // Generate very low amplitude sine wave for effective device warming
        const double frequency = 440.0; // A4 note
        const short amplitude = 32; // Very low amplitude (about 0.1% of max)

        for (int i = 0; i < samplesCount; i++)
        {
            var time = (double)i / sampleRate;
            var sineValue = Math.Sin(2 * Math.PI * frequency * time);
            var sample = (short)(sineValue * amplitude);
            writer.Write(sample);
        }

        return stream.ToArray();
    }

    // Ensure device preparation is complete before playback
    private async Task EnsureDeviceReadyAsync()
    {
        if (this._settings.PrepareDevice && this._devicePreparationTask != null)
        {
            try
            {
                await this._devicePreparationTask;
            }
            catch
            {
                // Device preparation failed, but continue with playback
            }
        }
    }

    public async Task PlayAudioStreamingAsync(byte[] audioData, Func<byte[], Task>? cacheCallback = null)
    {
        if (this._disposed)
            throw new ObjectDisposedException(nameof(AudioPlayer));

        // Ensure device is ready before starting playback
        await this.EnsureDeviceReadyAsync();

        try
        {
            this.StopAudio();

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

            this._wavePlayer = new WaveOutEvent();

            if (this._settings.OutputDevice >= 0)
            {
                ((WaveOutEvent)this._wavePlayer).DeviceNumber = this._settings.OutputDevice;
            }

            // Optimized buffering settings for minimal latency with stability
            ((WaveOutEvent)this._wavePlayer).DesiredLatency = 80; // 80ms buffer
            ((WaveOutEvent)this._wavePlayer).NumberOfBuffers = 2;  // Use 2 buffers

            this._wavePlayer.Volume = (float)Math.Max(0.0, Math.Min(1.0, this._settings.Volume));

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

            this._wavePlayer.PlaybackStopped += (sender, e) =>
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

            this._wavePlayer.Init(reader);

            // Minimal delay to ensure proper audio initialization
            await Task.Delay(20);

            this._wavePlayer.Play();

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
            this.StopAudio();
        }
    }

    public async Task PlayAudioSequentiallyAsync(List<byte[]> audioSegments)
    {
        if (this._disposed)
            throw new ObjectDisposedException(nameof(AudioPlayer));

        // Ensure device is ready before starting playback
        await this.EnsureDeviceReadyAsync();

        try
        {
            // Initialize single WavePlayer instance for all segments
            this._wavePlayer = new WaveOutEvent();

            if (this._settings.OutputDevice >= 0)
            {
                ((WaveOutEvent)this._wavePlayer).DeviceNumber = this._settings.OutputDevice;
            }

            // Optimized buffering settings for stability - slightly higher latency for reliability
            ((WaveOutEvent)this._wavePlayer).DesiredLatency = 100; // 100ms for stable initialization
            ((WaveOutEvent)this._wavePlayer).NumberOfBuffers = 3;   // 3 buffers for seamless transitions

            this._wavePlayer.Volume = (float)Math.Max(0.0, Math.Min(1.0, this._settings.Volume));

            foreach (var segment in audioSegments)
            {
                if (segment.Length == 0) continue;

                await this.PlaySegmentAsync(segment);
            }
        }
        finally
        {
            this.StopAudio();
        }
    }

    public async Task PlayAudioSequentiallyWithGenerationAsync(List<TextSegment> segments, Task? generationTask, FillerManager? fillerManager = null)
    {
        if (this._disposed)
            throw new ObjectDisposedException(nameof(AudioPlayer));

        // Ensure device is ready before starting playback
        await this.EnsureDeviceReadyAsync();

        try
        {
            // Initialize single WavePlayer instance for all segments
            this._wavePlayer = new WaveOutEvent();

            if (this._settings.OutputDevice >= 0)
            {
                ((WaveOutEvent)this._wavePlayer).DeviceNumber = this._settings.OutputDevice;
            }

            // Optimized buffering settings for stability - slightly higher latency for reliability
            ((WaveOutEvent)this._wavePlayer).DesiredLatency = 100; // 100ms for stable initialization
            ((WaveOutEvent)this._wavePlayer).NumberOfBuffers = 3;   // 3 buffers for seamless transitions

            this._wavePlayer.Volume = (float)Math.Max(0.0, Math.Min(1.0, this._settings.Volume));

            bool isFirstSegment = true;

            for (int i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];

                // Handle both cached and uncached segments
                if (segment.IsCached && segment.AudioData != null)
                {
                    // Play cached segments immediately
                    await this.PlaySegmentAsync(segment.AudioData, isFirstSegment);
                    isFirstSegment = false;
                }
                else
                {
                    // Segment not cached - play filler while waiting for generation
                    Console.WriteLine($"Waiting for segment {i + 1} to be generated...");

                    // Play filler while waiting for uncached segment
                    if (fillerManager != null)
                    {
                        try
                        {
                            var fillerAudio = await fillerManager.GetRandomFillerAudioAsync();
                            if (fillerAudio != null)
                            {
                                Console.WriteLine("Playing filler while waiting for segment generation...");
                                await this.PlaySegmentAsync(fillerAudio, isFirstSegment);
                                isFirstSegment = false;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Warning: Filler playback failed: {ex.Message}");
                        }
                    }

                    // Wait for this specific segment to be generated with safety checks
                    var waitStartTime = DateTime.UtcNow;
                    const int maxWaitTimeMs = 30000; // 30秒でタイムアウト

                    while ((!segment.IsCached || segment.AudioData == null) &&
                           (DateTime.UtcNow - waitStartTime).TotalMilliseconds < maxWaitTimeMs)
                    {
                        // Check if generation task completed (all segments done)
                        if (generationTask?.IsCompleted == true)
                        {
                            break;
                        }

                        await Task.Delay(100); // Check every 100ms
                    }

                    // Final check after waiting
                    if (segment.AudioData == null)
                    {
                        Console.WriteLine($"Warning: Segment {i + 1} could not be generated, skipping...");
                        continue;
                    }

                    // Play the generated segment
                    await this.PlaySegmentAsync(segment.AudioData, isFirstSegment);
                    isFirstSegment = false;
                }

                // After playing current segment, check if next segment needs filler
                if (i < segments.Count - 1) // Not the last segment
                {
                    var nextSegment = segments[i + 1];
                    if ((!nextSegment.IsCached || nextSegment.AudioData == null) && fillerManager != null)
                    {
                        try
                        {
                            var fillerAudio = await fillerManager.GetRandomFillerAudioAsync();
                            if (fillerAudio != null)
                            {
                                Console.WriteLine("Playing filler while waiting for next segment...");
                                await this.PlaySegmentAsync(fillerAudio, false);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Warning: Filler playback failed: {ex.Message}");
                        }
                    }
                }
            }

            // Ensure background generation is complete
            if (generationTask != null)
            {
                await generationTask;
            }
        }
        finally
        {
            this.StopAudio();
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

            if (this._wavePlayer != null)
            {
                this._wavePlayer.PlaybackStopped += (sender, e) =>
                {
                    reader?.Dispose();
                    if (e.Exception != null)
                    {
                        tcs.TrySetException(e.Exception);
                    }
                    else
                    {
                        tcs.TrySetResult(true);
                    }
                };
            }

            this._wavePlayer?.Init(reader);

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

            this._wavePlayer?.Play();

            await tcs.Task;

            // Ensure complete audio playback - increased delay for proper segment completion
            await Task.Delay(120);

            // Stop but don't dispose the WavePlayer - reuse for next segment
            this._wavePlayer?.Stop();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to play audio segment: {ex.Message}", ex);
        }
    }

    public async Task PlayAudioAsync(byte[] audioData)
    {
        if (this._disposed)
            throw new ObjectDisposedException(nameof(AudioPlayer));

        // Ensure device is ready before starting playback
        await this.EnsureDeviceReadyAsync();

        try
        {
            this.StopAudio();

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

            this._wavePlayer = new WaveOutEvent();

            if (this._settings.OutputDevice >= 0)
            {
                ((WaveOutEvent)this._wavePlayer).DeviceNumber = this._settings.OutputDevice;
            }

            // Optimized buffering settings for minimal latency with stability
            ((WaveOutEvent)this._wavePlayer).DesiredLatency = 80; // 80ms buffer
            ((WaveOutEvent)this._wavePlayer).NumberOfBuffers = 2;  // Use 2 buffers

            this._wavePlayer.Volume = (float)Math.Max(0.0, Math.Min(1.0, this._settings.Volume));

            var tcs = new TaskCompletionSource<bool>();

            this._wavePlayer.PlaybackStopped += (sender, e) =>
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

            this._wavePlayer.Init(reader);

            // Minimal delay to ensure proper audio initialization
            await Task.Delay(20);

            this._wavePlayer.Play();

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
            this.StopAudio();
        }
    }

    public void StopAudio()
    {
        try
        {
            if (this._wavePlayer != null)
            {
                this._wavePlayer.Stop();
                this._wavePlayer.Dispose();
                this._wavePlayer = null;
            }
        }
        catch
        {
        }
    }

    public static List<string> GetAvailableDevices()
    {
        try
        {
            // C# 13 Collection expression syntax
            return ["0: Default Device"];
        }
        catch
        {
            // C# 13 Empty collection expression
            return [];
        }
    }

    public void Dispose()
    {
        if (!this._disposed)
        {
            this.StopAudio();

            // Clean up device preparation task
            if (this._devicePreparationTask != null)
            {
                try
                {
                    this._devicePreparationTask.Wait(1000); // Wait up to 1 second
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }

            MediaFoundationApi.Shutdown();
            this._disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
