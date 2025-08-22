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

        Log.Information("PlayAudioSequentiallyWithGenerationAsync 開始 - セグメント数: {SegmentCount}, フィラーマネージャー: {HasFillerManager}",
            segments.Count, fillerManager != null);

        // Ensure device is ready before starting playback
        await this.EnsureDeviceReadyAsync();

        try
        {
            // Initialize single WavePlayer instance for all segments
            this._wavePlayer = this.CreateWavePlayer();

            this._wavePlayer.Volume = (float)Math.Max(0.0, Math.Min(1.0, this._settings.Volume));

            bool isFirstSegment = true;
            bool lastSegmentHadFiller = false; // 前のセグメントでフィラーを再生したかどうかを追跡

            for (int i = 0; i < segments.Count; i++)
            {
                var segment = segments[i];
                Log.Information("セグメント {SegmentNumber}/{Total} を処理中: \"{Text}\" (キャッシュ済み: {IsCached})",
                    i + 1, segments.Count, segment.Text, segment.IsCached);

                // Handle both cached and uncached segments
                if (segment.IsCached && segment.AudioData != null)
                {
                    Log.Debug("キャッシュ済みセグメント {SegmentNumber} を再生します", i + 1);
                    // Play cached segments immediately
                    await this.PlaySegmentAsync(segment.AudioData, isFirstSegment, cancellationToken);
                    isFirstSegment = false;
                    lastSegmentHadFiller = false; // キャッシュ済みセグメントの後はフィラー不要
                    Log.Debug("キャッシュ済みセグメント {SegmentNumber} の再生が完了しました", i + 1);
                }
                else
                {
                    // Segment not cached - play filler while waiting for generation
                    Log.Information("セグメント {SegmentNumber} の生成を待機中...", i + 1);

                    // Play filler while waiting for uncached segment (前のセグメントでフィラーを再生していない場合のみ)
                    if (fillerManager != null && !lastSegmentHadFiller)
                    {
                        try
                        {
                            Log.Debug("フィラー音声の取得を開始します");
                            var fillerAudio = await fillerManager.GetRandomFillerAudioAsync();
                            if (fillerAudio != null)
                            {
                                Log.Information("セグメント生成待機中にフィラー音声を再生します (サイズ: {Size} bytes)", fillerAudio.Length);
                                await this.PlaySegmentAsync(fillerAudio, isFirstSegment, cancellationToken);
                                isFirstSegment = false;
                                lastSegmentHadFiller = true; // フィラーを再生したことを記録
                                Log.Information("フィラー音声の再生が完了しました");
                            }
                            else
                            {
                                Log.Warning("フィラー音声の取得に失敗しました");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "フィラー音声の再生に失敗しました");
                        }
                    }
                    else
                    {
                        Log.Debug("フィラー音声は再生しません (前回フィラー再生済み: {LastHadFiller})", lastSegmentHadFiller);
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

                            Log.Information("セグメント {SegmentNumber} の音声生成を開始します", i + 1);
                            var result = await processingChannel.ProcessAudioAsync(segmentRequest, cancellationToken);
                            if (result.Success && result.AudioData.Length > 0)
                            {
                                segment.AudioData = result.AudioData;
                                segment.IsCached = true;
                                Log.Information("セグメント {SegmentNumber} の生成が完了しました (サイズ: {Size} bytes)", i + 1, result.AudioData.Length);
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
                            Log.Error(ex, "セグメント {SegmentNumber} の生成に失敗しました。スキップします", i + 1);
                            continue;
                        }
                    }
                    else
                    {
                        // Fallback: wait for segment to be marked as ready (for backward compatibility)
                        var waitStartTime = DateTime.UtcNow;
                        const int maxWaitTimeMs = 30000; // 30秒でタイムアウト

                        Log.Information("フォールバック処理: セグメント {SegmentNumber} の準備完了を待機中...", i + 1);
                        while ((!segment.IsCached || segment.AudioData == null) &&
                               (DateTime.UtcNow - waitStartTime).TotalMilliseconds < maxWaitTimeMs)
                        {
                            await Task.Delay(100, cancellationToken);
                            cancellationToken.ThrowIfCancellationRequested();
                        }

                        if ((DateTime.UtcNow - waitStartTime).TotalMilliseconds >= maxWaitTimeMs)
                        {
                            Log.Warning("セグメント {SegmentNumber} の待機がタイムアウトしました", i + 1);
                        }
                    }

                    // Final check after waiting
                    if (segment.AudioData == null)
                    {
                        Log.Warning("セグメント {SegmentNumber} の生成に失敗しました。スキップします", i + 1);
                        continue;
                    }

                    // Play the generated segment
                    Log.Information("生成されたセグメント {SegmentNumber} を再生します", i + 1);
                    await this.PlaySegmentAsync(segment.AudioData, isFirstSegment, cancellationToken);
                    isFirstSegment = false;
                    lastSegmentHadFiller = false; // 生成されたセグメントの後はフィラー不要
                    Log.Information("生成されたセグメント {SegmentNumber} の再生が完了しました", i + 1);
                }

                // After playing current segment, check if next segment needs filler
                // ただし、前のセグメントでフィラーを再生していない場合のみ
                if (i < segments.Count - 1 && !lastSegmentHadFiller) // Not the last segment and no filler was played recently
                {
                    var nextSegment = segments[i + 1];
                    if ((!nextSegment.IsCached || nextSegment.AudioData == null) && fillerManager != null)
                    {
                        try
                        {
                            Log.Debug("次のセグメント待機中にフィラー音声を再生します");
                            var fillerAudio = await fillerManager.GetRandomFillerAudioAsync();
                            if (fillerAudio != null)
                            {
                                Log.Information("次のセグメント待機中にフィラー音声を再生します (サイズ: {Size} bytes)", fillerAudio.Length);
                                await this.PlaySegmentAsync(fillerAudio, false, cancellationToken);
                                lastSegmentHadFiller = true; // フィラーを再生したことを記録
                                Log.Information("次のセグメント待機中のフィラー音声の再生が完了しました");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "フィラー音声の再生に失敗しました");
                        }
                    }
                }

                // セグメント間の適切な間隔を確保
                if (i < segments.Count - 1)
                {
                    Log.Debug("セグメント間の間隔を確保中 (50ms)");
                    await Task.Delay(50, cancellationToken); // 50msの間隔
                }
            }

            // No need to wait for background generation - channel handles it synchronously
            Log.Information("全セグメントの再生が完了しました");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PlayAudioSequentiallyWithGenerationAsync でエラーが発生しました");
            throw;
        }
        finally
        {
            Log.Debug("AudioPlayer の停止処理を開始します");
            this.StopAudio();
            Log.Debug("AudioPlayer の停止処理が完了しました");
        }
    }

    private async Task PlaySegmentAsync(byte[] audioData, bool isFirstSegment = false, CancellationToken cancellationToken = default)
    {
        try
        {
            Log.Debug("PlaySegmentAsync 開始 - サイズ: {Size} bytes, 最初のセグメント: {IsFirst}", audioData.Length, isFirstSegment);

            using var audioStream = new MemoryStream(audioData);
            WaveStream reader;

            // Try to detect if it's MP3 or WAV by reading the header using Memory for async operations
            audioStream.Position = 0;
            var headerBuffer = new byte[12];
            var bytesRead = await audioStream.ReadAsync(headerBuffer, 0, 12);
            audioStream.Position = 0;

            var format = AudioConversionUtility.DetectFormat(headerBuffer);
            Log.Debug("音声フォーマットを検出: {Format}", format);

            if (format == AudioFormat.WAV)
            {
                reader = new WaveFileReader(audioStream);
                Log.Debug("WaveFileReader を作成しました");
            }
            else if (format == AudioFormat.MP3)
            {
                reader = new Mp3FileReader(audioStream);
                Log.Debug("Mp3FileReader を作成しました");
            }
            else
            {
                // Try MP3 first since we're primarily caching MP3 files now
                try
                {
                    audioStream.Position = 0;
                    reader = new Mp3FileReader(audioStream);
                    Log.Debug("MP3 フォールバックで Mp3FileReader を作成しました");
                }
                catch
                {
                    // Fall back to WAV if MP3 fails
                    audioStream.Position = 0;
                    reader = new WaveFileReader(audioStream);
                    Log.Debug("WAV フォールバックで WaveFileReader を作成しました");
                }
            }

            var tcs = new TaskCompletionSource<bool>();

            if (this._wavePlayer != null)
            {
                EventHandler<StoppedEventArgs>? handler = null;
                handler = (sender, e) =>
                {
                    Log.Debug("PlaybackStopped イベントが発生しました - 例外: {Exception}", e.Exception?.Message ?? "なし");
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
                Log.Debug("PlaybackStopped イベントハンドラーを登録しました");
            }

            Log.Debug("WavePlayer に音声リーダーを初期化中...");
            this._wavePlayer?.Init(reader);

            // First segment needs longer initialization for audio device setup
            if (isFirstSegment)
            {
                // Extended delay for first segment to ensure proper audio device initialization
                // Wait for pre-warming to complete if still in progress
                Log.Debug("最初のセグメントのため、200ms の遅延を実行中...");
                await Task.Delay(200, cancellationToken); // 200ms for device initialization and stability
            }
            else
            {
                // Minimal delay for subsequent segments
                Log.Debug("後続セグメントのため、20ms の遅延を実行中...");
                await Task.Delay(20, cancellationToken); // 20msに増加して安定性を向上
            }

            cancellationToken.ThrowIfCancellationRequested();
            Log.Debug("音声再生を開始します");
            this._wavePlayer?.Play();

            using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
            {
                // タイムアウトを設定して無限待機を防ぐ
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                Log.Debug("音声再生完了を待機中 (タイムアウト: 30秒)...");
                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    Log.Warning("セグメント再生がタイムアウトしました。強制停止します。");
                    this._wavePlayer?.Stop();
                    throw new TimeoutException("セグメント再生がタイムアウトしました");
                }

                Log.Debug("音声再生が完了しました。実際の完了を待機中...");
                await tcs.Task; // 実際の完了を待機
            }

            // Ensure complete audio playback - 適切な遅延を設定
            var playbackDelay = isFirstSegment ? 150 : 100; // 最初のセグメントは長めの遅延
            Log.Debug("音声再生完了後の遅延を実行中: {Delay}ms", playbackDelay);
            await Task.Delay(playbackDelay, cancellationToken);

            // Stop but don't dispose the WavePlayer - reuse for next segment
            Log.Debug("WavePlayer を停止中...");
            this._wavePlayer?.Stop();

            Log.Information("セグメント再生が完了しました (遅延: {Delay}ms)", playbackDelay);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "セグメント再生中にエラーが発生しました");
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
            Log.Information("Detected audio format: {Format}, Header: {Header}", format, BitConverter.ToString(headerBuffer));

            if (format == AudioFormat.WAV)
            {
                Log.Information("Creating WaveFileReader for WAV format");
                reader = new WaveFileReader(audioStream);
            }
            else if (format == AudioFormat.MP3)
            {
                Log.Information("Creating Mp3FileReader for MP3 format");
                reader = new Mp3FileReader(audioStream);
            }
            else
            {
                Log.Information("Unknown format, trying MP3 first, then WAV fallback");
                // Try MP3 first since we're primarily caching MP3 files now
                try
                {
                    audioStream.Position = 0;
                    reader = new Mp3FileReader(audioStream);
                    Log.Information("MP3 fallback successful");
                }
                catch (Exception ex)
                {
                    Log.Warning("MP3 fallback failed: {Error}, trying WAV", ex.Message);
                    // Fall back to WAV if MP3 fails
                    audioStream.Position = 0;
                    reader = new WaveFileReader(audioStream);
                    Log.Information("WAV fallback successful");
                }
            }

            Log.Information("Creating WavePlayer for audio playback");
            this._wavePlayer = this.CreateWavePlayer();

            this._wavePlayer.Volume = (float)Math.Max(0.0, Math.Min(1.0, this._settings.Volume));
            Log.Information("WavePlayer volume set to: {Volume}", this._wavePlayer.Volume);

            var tcs = new TaskCompletionSource<bool>();

            this._wavePlayer.PlaybackStopped += (sender, e) =>
            {
                Log.Information("PlaybackStopped event fired, Exception: {Exception}", e.Exception?.Message ?? "None");
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

            Log.Information("Initializing WavePlayer with audio reader");
            this._wavePlayer.Init(reader);

            // Minimal delay to ensure proper audio initialization
            Log.Information("Waiting for audio initialization...");
            await Task.Delay(20, cancellationToken);

            Log.Information("Starting audio playback");
            this._wavePlayer.Play();

            Log.Information("Waiting for playback completion...");
            await tcs.Task.ConfigureAwait(false);
            Log.Information("Playback completed, waiting for buffer flush...");

            // Ensure all buffered audio is played before stopping
            await Task.Delay(150, cancellationToken); // Wait for buffer to flush
            Log.Information("Buffer flush completed");
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
