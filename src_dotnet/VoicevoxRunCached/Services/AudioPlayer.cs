using NAudio.Wave;
using NAudio.MediaFoundation;
using NAudio.CoreAudioApi;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;
using System.Text;
using Serilog;

// C# 13 Using alias for commonly used types
using WavePlayer = NAudio.Wave.WaveOutEvent;
using AudioStream = System.IO.MemoryStream;

namespace VoicevoxRunCached.Services;

public class AudioPlayer : IDisposable
{
    private readonly AudioSettings _settings;
    private IWavePlayer? _wavePlayer;
    private MMDevice? _wasapiDevice;
    private bool _disposed;
    private Task? _devicePreparationTask;

    public AudioPlayer(AudioSettings settings)
    {
        // C# 13 nameof expression for type-safe parameter validation
        this._settings = settings ?? throw new ArgumentNullException(nameof(settings));
        MediaFoundationManager.EnsureInitialized();

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

    private IWavePlayer CreateWavePlayer()
    {
        // Prefer WASAPI endpoint ID when provided; fallback to WaveOutEvent
        if (!String.IsNullOrWhiteSpace(this._settings.OutputDeviceId))
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                this._wasapiDevice = enumerator.GetDevice(this._settings.OutputDeviceId);
                return new WasapiOut(this._wasapiDevice, AudioClientShareMode.Shared, false, 100);
            }
            catch
            {
                // Fallback silently to WaveOutEvent
            }
        }

        var waveOut = new WaveOutEvent
        {
            DesiredLatency = 100,
            NumberOfBuffers = 3
        };
        if (this._settings.OutputDevice >= 0)
        {
            waveOut.DeviceNumber = this._settings.OutputDevice;
        }
        return waveOut;
    }

    private async Task PrewarmAudioDeviceAsync(int durationMs = 100)
    {
        try
        {
            // Create a silent audio to initialize the device with configurable duration
            var generateSilence = this._settings.PreparationVolume <= 0;
            var silentWavData = AudioConversionUtility.CreateMinimalWavData(durationMs, generateSilence: generateSilence);

            using var audioStream = new MemoryStream(silentWavData);
            using var reader = new WaveFileReader(audioStream);
            using var wavePlayer = new WaveOutEvent();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)); // 5秒でタイムアウト

            if (this._settings.OutputDevice >= 0)
            {
                wavePlayer.DeviceNumber = this._settings.OutputDevice;
            }

            // Use same buffer settings as main playback
            wavePlayer.DesiredLatency = 100;
            wavePlayer.NumberOfBuffers = 3;

            // Use very low but audible volume for effective device warming, or silent if volume is 0
            if (this._settings.PreparationVolume <= 0)
            {
                wavePlayer.Volume = 0.0f; // Completely silent
            }
            else
            {
                wavePlayer.Volume = (float)Math.Max(0.001, Math.Min(1.0, this._settings.PreparationVolume));
            }

            var tcs = new TaskCompletionSource<bool>();

            wavePlayer.PlaybackStopped += (sender, e) =>
            {
                tcs.TrySetResult(true);
            };

            wavePlayer.Init(reader);
            wavePlayer.Play();

            // Wait for pre-warming to complete with timeout
            await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Log.Debug("Device pre-warming timed out");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Device pre-warming failed");
        }
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

    public async Task PlayAudioStreamingAsync(byte[] audioData, Func<byte[], Task>? cacheCallback = null, CancellationToken cancellationToken = default)
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
            audioStream.Position = 0;

            var format = AudioConversionUtility.DetectFormat(headerBuffer);

            if (format == AudioFormat.WAV)
            {
                reader = new WaveFileReader(audioStream);
            }
            else if (format == AudioFormat.MP3)
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

            this._wavePlayer = this.CreateWavePlayer();

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
                        Log.Warning(ex, "音声のキャッシュ保存に失敗しました");
                    }
                });
            }

            EventHandler<StoppedEventArgs>? handler = null;
            handler = (sender, e) =>
            {
                try { reader.Dispose(); } catch { }
                if (e.Exception != null)
                {
                    tcs.TrySetException(e.Exception);
                }
                else
                {
                    tcs.TrySetResult(true);
                }
                if (this._wavePlayer != null && handler != null)
                {
                    this._wavePlayer.PlaybackStopped -= handler;
                }
            };
            this._wavePlayer.PlaybackStopped += handler;

            this._wavePlayer.Init(reader);

            // Minimal delay to ensure proper audio initialization
            await Task.Delay(20, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            this._wavePlayer.Play();

            using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
            {
                await tcs.Task.ConfigureAwait(false);
            }

            // Ensure all buffered audio is played before stopping
            await Task.Delay(150, cancellationToken); // Wait for buffer to flush

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

    public async Task PlayAudioSequentiallyAsync(List<byte[]> audioSegments, CancellationToken cancellationToken = default)
    {
        if (this._disposed)
            throw new ObjectDisposedException(nameof(AudioPlayer));

        // Ensure device is ready before starting playback
        await this.EnsureDeviceReadyAsync();

        try
        {
            // Initialize single WavePlayer instance for all segments
            this._wavePlayer = this.CreateWavePlayer();

            this._wavePlayer.Volume = (float)Math.Max(0.0, Math.Min(1.0, this._settings.Volume));

            foreach (var segment in audioSegments)
            {
                if (segment.Length == 0) continue;

                await this.PlaySegmentAsync(segment, cancellationToken: cancellationToken);
            }
        }
        finally
        {
            this.StopAudio();
        }
    }

    public async Task PlayAudioSequentiallyWithGenerationAsync(List<TextSegment> segments, AudioProcessingChannel? processingChannel, FillerManager? fillerManager = null, CancellationToken cancellationToken = default)
    {
        if (this._disposed)
            throw new ObjectDisposedException(nameof(AudioPlayer));

        // Ensure device is ready before starting playback
        await this.EnsureDeviceReadyAsync();

        try
        {
            // Initialize single WavePlayer instance for all segments
            this._wavePlayer = this.CreateWavePlayer();

            this._wavePlayer.Volume = (float)Math.Max(0.0, Math.Min(1.0, this._settings.Volume));

            bool isFirstSegment = true;

            for (int i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];

                // Handle both cached and uncached segments
                if (segment.IsCached && segment.AudioData != null)
                {
                    // Play cached segments immediately
                    await this.PlaySegmentAsync(segment.AudioData, isFirstSegment, cancellationToken);
                    isFirstSegment = false;
                }
                else
                {
                    // Segment not cached - play filler while waiting for generation
                    Log.Debug("セグメント {SegmentNumber} の生成を待機中...", i + 1);

                    // Play filler while waiting for uncached segment
                    if (fillerManager != null)
                    {
                        try
                        {
                            var fillerAudio = await fillerManager.GetRandomFillerAudioAsync();
                            if (fillerAudio != null)
                            {
                                Log.Debug("セグメント生成待機中にフィラー音声を再生します");
                                await this.PlaySegmentAsync(fillerAudio, isFirstSegment, cancellationToken);
                                isFirstSegment = false;
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "フィラー音声の再生に失敗しました");
                        }
                    }

                    // Use channel-based waiting instead of polling
                    if (processingChannel != null)
                    {
                        try
                        {
                            var segmentRequest = new VoiceRequest
                            {
                                Text = segment.Text,
                                SpeakerId = segment.SpeakerId ?? 1, // Default fallback
                                Speed = 1.0,
                                Pitch = 0.0,
                                Volume = 1.0
                            };

                            var result = await processingChannel.ProcessAudioAsync(segmentRequest, cancellationToken);
                            if (result.Success && result.AudioData.Length > 0)
                            {
                                segment.AudioData = result.AudioData;
                                segment.IsCached = true;
                                Log.Debug("セグメント {SegmentNumber} の生成が完了しました", i + 1);
                            }
                            else
                            {
                                Log.Warning("セグメント {SegmentNumber} の生成に失敗しました: {Error}", i + 1, result.ErrorMessage ?? "Unknown error");
                                continue;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            Log.Information("セグメント生成がキャンセルされました");
                            throw;
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "セグメント {SegmentNumber} の生成に失敗しました。スキップします", i + 1);
                            continue;
                        }
                    }
                    else
                    {
                        // Fallback: wait for segment to be marked as ready (for backward compatibility)
                        var waitStartTime = DateTime.UtcNow;
                        const int maxWaitTimeMs = 30000; // 30秒でタイムアウト

                        while ((!segment.IsCached || segment.AudioData == null) &&
                               (DateTime.UtcNow - waitStartTime).TotalMilliseconds < maxWaitTimeMs)
                        {
                            await Task.Delay(100, cancellationToken);
                        }
                    }

                    // Final check after waiting
                    if (segment.AudioData == null)
                    {
                        Log.Warning("セグメント {SegmentNumber} の生成に失敗しました。スキップします", i + 1);
                        continue;
                    }

                    // Play the generated segment
                    await this.PlaySegmentAsync(segment.AudioData, isFirstSegment, cancellationToken);
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
                                Log.Debug("次のセグメント待機中にフィラー音声を再生します");
                                await this.PlaySegmentAsync(fillerAudio, false, cancellationToken);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "フィラー音声の再生に失敗しました");
                        }
                    }
                }
            }

            // No need to wait for background generation - channel handles it synchronously
        }
        finally
        {
            this.StopAudio();
        }
    }

    private async Task PlaySegmentAsync(byte[] audioData, bool isFirstSegment = false, CancellationToken cancellationToken = default)
    {
        try
        {
            using var audioStream = new MemoryStream(audioData);
            WaveStream reader;

            // Try to detect if it's MP3 or WAV by reading the header using Memory for async operations
            audioStream.Position = 0;
            var headerBuffer = new byte[12];
            var bytesRead = await audioStream.ReadAsync(headerBuffer, 0, 12);
            audioStream.Position = 0;

            var format = AudioConversionUtility.DetectFormat(headerBuffer);

            if (format == AudioFormat.WAV)
            {
                reader = new WaveFileReader(audioStream);
            }
            else if (format == AudioFormat.MP3)
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
                EventHandler<StoppedEventArgs>? handler = null;
                handler = (sender, e) =>
                {
                    try { reader?.Dispose(); } catch { }
                    if (e.Exception != null)
                    {
                        tcs.TrySetException(e.Exception);
                    }
                    else
                    {
                        tcs.TrySetResult(true);
                    }
                    if (this._wavePlayer != null && handler != null)
                    {
                        this._wavePlayer.PlaybackStopped -= handler;
                    }
                };
                this._wavePlayer.PlaybackStopped += handler;
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

            cancellationToken.ThrowIfCancellationRequested();
            this._wavePlayer?.Play();

            using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
            {
                await tcs.Task;
            }

            // Ensure complete audio playback - increased delay for proper segment completion
            await Task.Delay(120, cancellationToken);

            // Stop but don't dispose the WavePlayer - reuse for next segment
            this._wavePlayer?.Stop();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to play audio segment: {ex.Message}", ex);
        }
    }

    public async Task PlayAudioAsync(byte[] audioData, CancellationToken cancellationToken = default)
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
            audioStream.Position = 0;

            var format = AudioConversionUtility.DetectFormat(headerBuffer);

            if (format == AudioFormat.WAV)
            {
                reader = new WaveFileReader(audioStream);
            }
            else if (format == AudioFormat.MP3)
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

            this._wavePlayer = this.CreateWavePlayer();

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
            await Task.Delay(20, cancellationToken);

            this._wavePlayer.Play();

            await tcs.Task.ConfigureAwait(false);

            // Ensure all buffered audio is played before stopping
            await Task.Delay(150, cancellationToken); // Wait for buffer to flush
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
                if (this._wasapiDevice != null)
                {
                    try { this._wasapiDevice.Dispose(); } catch { }
                    this._wasapiDevice = null;
                }
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
            // Keep simple default device listing to avoid platform-specific enumeration issues
            return ["-1: Default Device"];
        }
        catch
        {
            return [];
        }
    }

    public void Dispose()
    {
        if (!this._disposed)
        {
            try
            {
                this.StopAudio();

                // Clean up device preparation task
                if (this._devicePreparationTask != null)
                {
                    try
                    {
                        // 非同期タスクの適切な破棄
                        if (!this._devicePreparationTask.IsCompleted)
                        {
                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                            this._devicePreparationTask.Wait(cts.Token);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Log.Debug("Device preparation task cleanup timed out");
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Error during device preparation task cleanup");
                    }
                }

                // Clean up WASAPI device
                if (this._wasapiDevice != null)
                {
                    try
                    {
                        this._wasapiDevice.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Error disposing WASAPI device");
                    }
                    finally
                    {
                        this._wasapiDevice = null;
                    }
                }

                this._disposed = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error during AudioPlayer disposal");
            }
            finally
            {
                GC.SuppressFinalize(this);
            }
        }
    }

    // Finalizer for safety
    ~AudioPlayer()
    {
        this.Dispose(false);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!this._disposed)
        {
            if (disposing)
            {
                // Dispose managed resources
                this.Dispose();
            }
            else
            {
                // Finalizer called - dispose unmanaged resources only
                try
                {
                    this.StopAudio();
                }
                catch
                {
                    // Ignore errors in finalizer
                }
            }
        }
    }
}
