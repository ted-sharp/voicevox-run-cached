using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.MediaFoundation;
using System.Text.Json;

namespace VoicevoxRunCached.Services;

public class CommandHandler
{
    private readonly AppSettings _settings;
    private readonly ILogger _logger;

    public CommandHandler(AppSettings settings, ILogger logger)
    {
        this._settings = settings;
        this._logger = logger;
    }

    public async Task<int> HandleSpeakersAsync()
    {
        try
        {
            // Ensure VOICEVOX engine is running
            using var engineManager = new VoiceVoxEngineManager(this._settings.VoiceVox);
            if (!await engineManager.EnsureEngineRunningAsync())
            {
                ConsoleHelper.WriteError("Error: VOICEVOX engine is not available", this._logger);
                return 1;
            }

            using var apiClient = new VoiceVoxApiClient(this._settings.VoiceVox);
            var speakers = await apiClient.GetSpeakersAsync();

            ConsoleHelper.WriteLine("Available speakers:", this._logger);
            foreach (var speaker in speakers)
            {
                ConsoleHelper.WriteLine($"  {speaker.Name} (v{speaker.Version})", this._logger);
                foreach (var style in speaker.Styles)
                {
                    ConsoleHelper.WriteLine($"    ID: {style.Id} - {style.Name}", this._logger);
                }
                Console.WriteLine();
            }
            return 0;
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"Error: {ex.Message}", this._logger);
            return 1;
        }
    }

    public int HandleDevices(string[] args)
    {
        try
        {
            bool outputJson = args.Contains("--json");
            bool full = args.Contains("--full");

            using var enumerator = new MMDeviceEnumerator();

            // Default endpoint (may throw depending on environment)
            string? defaultName = null;
            string? defaultId = null;
            try
            {
                var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                defaultName = def?.FriendlyName ?? "Default Device";
                defaultId = def?.ID ?? "";
            }
            catch (Exception ex)
            {
                ConsoleHelper.WriteWarning($"Failed to get default audio endpoint: {ex.Message}", this._logger);
            }

            var list = new List<object>();
            if (full)
            {
                try
                {
                    var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                    foreach (var d in devices)
                    {
                        try
                        {
                            list.Add(new
                            {
                                id = d.ID,
                                name = d.FriendlyName,
                                state = d.State.ToString()
                            });
                        }
                        catch (Exception inner)
                        {
                            ConsoleHelper.WriteWarning($"Failed to read device info: {inner.Message}", this._logger);
                        }
                    }
                }
                catch (Exception ex)
                {
                    ConsoleHelper.WriteWarning($"Failed to enumerate audio endpoints: {ex.Message}", this._logger);
                }
            }

            if (outputJson)
            {
                var payload = new
                {
                    @default = new { id = defaultId, name = defaultName },
                    devices = list
                };
                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
                Console.WriteLine(json);
                return 0;
            }

            Console.WriteLine("Available output devices (WASAPI):");
            if (!String.IsNullOrEmpty(defaultName))
            {
                Console.WriteLine($"  Default: \"{defaultName}\" (ID: {defaultId})");
            }
            else
            {
                Console.WriteLine("  Default: (unavailable)");
            }

            if (full)
            {
                if (list.Count == 0)
                {
                    Console.WriteLine("  (no active render devices)");
                }
                else
                {
                    int idx = 0;
                    foreach (var item in list)
                    {
                        var id = (string?)item.GetType().GetProperty("id")?.GetValue(item) ?? "";
                        var name = (string?)item.GetType().GetProperty("name")?.GetValue(item) ?? "";
                        var state = (string?)item.GetType().GetProperty("state")?.GetValue(item) ?? "";
                        Console.WriteLine($"  [{idx}] \"{name}\" (ID: {id}) State: {state}");
                        idx++;
                    }
                }
            }
            else
            {
                Console.WriteLine("  (use 'devices --full' for a detailed list)");
            }
            return 0;
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"Error listing devices: {ex.Message}", this._logger);
            return 1;
        }
    }

    public async Task<int> HandleInitAsync()
    {
        try
        {
            // Ensure VOICEVOX engine is running
            using var engineManager = new VoiceVoxEngineManager(this._settings.VoiceVox);
            if (!await engineManager.EnsureEngineRunningAsync())
            {
                ConsoleHelper.WriteError("Error: VOICEVOX engine is not available", this._logger);
                return 1;
            }

            var cacheManager = new AudioCacheManager(this._settings.Cache);
            var fillerManager = new FillerManager(this._settings.Filler, cacheManager, this._settings.VoiceVox.DefaultSpeaker);

            ConsoleHelper.WriteLine("Initializing filler cache...", this._logger);
            await fillerManager.InitializeFillerCacheAsync(this._settings);
            ConsoleHelper.WriteSuccess("Filler cache initialized", this._logger);
            return 0;
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"Error initializing filler cache: {ex.Message}", this._logger);
            return 1;
        }
    }

    public async Task<int> HandleClearCacheAsync()
    {
        try
        {
            using var spinner = new ProgressSpinner("Clearing audio cache...");
            var cacheManager = new AudioCacheManager(this._settings.Cache);

            await cacheManager.ClearAllCacheAsync();

            // Also clear filler cache using configured filler directory
            var fillerManager = new FillerManager(this._settings.Filler, cacheManager, this._settings.VoiceVox.DefaultSpeaker);
            await fillerManager.ClearFillerCacheAsync();

            ConsoleHelper.WriteSuccess("Cache cleared successfully!", this._logger);
            return 0;
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"Error clearing cache: {ex.Message}", this._logger);
            return 1;
        }
    }

    public async Task<int> HandleBenchmarkAsync()
    {
        try
        {
            // Ensure VOICEVOX engine is running
            using var engineManager = new VoiceVoxEngineManager(this._settings.VoiceVox);
            if (!await engineManager.EnsureEngineRunningAsync())
            {
                ConsoleHelper.WriteError("Error: VOICEVOX engine is not available", this._logger);
                return 1;
            }

            ConsoleHelper.WriteLine("Starting performance benchmark...", this._logger);

            // Warm-up
            ConsoleHelper.WriteLine("Warming up...", this._logger);

            using var apiClient = new VoiceVoxApiClient(this._settings.VoiceVox);
            await apiClient.InitializeSpeakerAsync(this._settings.VoiceVox.DefaultSpeaker);

            // Benchmark
            ConsoleHelper.WriteLine("Benchmarking...", this._logger);
            var segments = new List<TextSegment>
            {
                new TextSegment { Text = "Hello, this is a performance benchmark." },
                new TextSegment { Text = "This is a longer text to test the caching mechanism." },
                new TextSegment { Text = "And another segment to ensure the pipeline is efficient." }
            };

            using var spinner = new ProgressSpinner("Benchmarking...");
            var totalStartTime = DateTime.UtcNow;

            for (int i = 0; i < 10; i++) // Run benchmark 10 times
            {
                spinner.UpdateMessage($"Benchmark iteration {i + 1}/10");
                var request = new VoiceRequest
                {
                    Text = segments[i % segments.Count].Text, // Cycle through segments
                    SpeakerId = this._settings.VoiceVox.DefaultSpeaker,
                    Speed = 1.0,
                    Pitch = 0.0,
                    Volume = 1.0
                };

                var audioQuery = await apiClient.GenerateAudioQueryAsync(request, CancellationToken.None);
                var audioData = await apiClient.SynthesizeAudioAsync(audioQuery, request.SpeakerId, CancellationToken.None);
            }

            var elapsedTime = (DateTime.UtcNow - totalStartTime).TotalMilliseconds;
            ConsoleHelper.WriteSuccess($"Benchmark completed. Total time: {elapsedTime:F1}ms", this._logger);
            return 0;
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"Error during benchmark: {ex.Message}", this._logger);
            return 1;
        }
    }

    public async Task<int> HandleTextToSpeechAsync(VoiceRequest request, bool noCache, bool cacheOnly, bool verbose = false, string? outPath = null, bool noPlay = false, CancellationToken cancellationToken = default)
    {
        var totalStartTime = DateTime.UtcNow;
        try
        {
            // Ensure VOICEVOX engine is running
            var engineStartTime = DateTime.UtcNow;
            using var engineManager = new VoiceVoxEngineManager(this._settings.VoiceVox);
            if (cancellationToken.IsCancellationRequested || !await engineManager.EnsureEngineRunningAsync())
            {
                ConsoleHelper.WriteError("Error: VOICEVOX engine is not available", this._logger);
                return 1;
            }

            if (verbose)
            {
                ConsoleHelper.WriteLine($"Engine check completed in {(DateTime.UtcNow - engineStartTime).TotalMilliseconds:F1}ms", this._logger);
            }

            // If output file specified, start background export task (single-shot full text generation)
            Task? exportTask = null;
            if (!String.IsNullOrWhiteSpace(outPath))
            {
                exportTask = Task.Run(async () =>
                {
                    try
                    {
                        using var apiClient = new VoiceVoxApiClient(this._settings.VoiceVox);
                        await apiClient.InitializeSpeakerAsync(request.SpeakerId, cancellationToken);
                        var audioQuery = await apiClient.GenerateAudioQueryAsync(request, cancellationToken);
                        var wavData = await apiClient.SynthesizeAudioAsync(audioQuery, request.SpeakerId, cancellationToken);
                        await this.WriteOutputFileAsync(wavData, outPath!, cancellationToken);
                        ConsoleHelper.WriteSuccess($"Saved output to: {outPath}", this._logger);
                    }
                    catch (OperationCanceledException)
                    {
                        ConsoleHelper.WriteLine("Output export was cancelled", this._logger);
                    }
                    catch (Exception ex)
                    {
                        ConsoleHelper.WriteWarning($"Failed to save output to '{outPath}': {ex.Message}", this._logger);
                    }
                }, cancellationToken);
            }

            if (noPlay)
            {
                if (exportTask != null)
                {
                    await exportTask;
                }
                ConsoleHelper.WriteSuccess("Done (no-play mode)!", this._logger);
                if (verbose)
                {
                    ConsoleHelper.WriteLine($"Total execution time: {(DateTime.UtcNow - totalStartTime).TotalMilliseconds:F1}ms", this._logger);
                }
                return 0;
            }

            var cacheManager = new AudioCacheManager(this._settings.Cache);
            byte[]? audioData = null;

            // Process text in segments for better cache efficiency
            if (!noCache)
            {
                var segmentStartTime = DateTime.UtcNow;
                ConsoleHelper.WriteLine("Processing segments...", this._logger);
                cancellationToken.ThrowIfCancellationRequested();

                // テキストセグメントを分割
                var textProcessor = new TextSegmentProcessor();
                var segments = await textProcessor.ProcessTextAsync(request.Text, request.SpeakerId, cancellationToken);

                // セグメントの位置情報を更新
                textProcessor.UpdateSegmentPositions(segments);

                // キャッシュからセグメントを処理
                var processedSegments = await cacheManager.ProcessTextSegmentsAsync(segments, request, cancellationToken);

                if (verbose)
                {
                    ConsoleHelper.WriteLine($"Segment processing completed in {(DateTime.UtcNow - segmentStartTime).TotalMilliseconds:F1}ms", this._logger);
                }
                var cachedCount = processedSegments.Count(s => s.IsCached);
                var totalCount = processedSegments.Count;

                if (cachedCount > 0)
                {
                    ConsoleHelper.WriteSuccess($"Found {cachedCount}/{totalCount} segments in cache!", this._logger);
                }

                var uncachedSegments = processedSegments.Where(s => !s.IsCached).ToList();

                // Start background generation for uncached segments
                AudioProcessingChannel? processingChannel = null;
                if (uncachedSegments.Count > 0)
                {
                    if (cacheOnly)
                    {
                        ConsoleHelper.WriteError($"{uncachedSegments.Count} segments not cached and --cache-only specified", this._logger);
                        return 1;
                    }

                    ConsoleHelper.WriteWarning($"Generating {uncachedSegments.Count} segments using processing channel...", this._logger);
                    processingChannel = new AudioProcessingChannel(cacheManager, new VoiceVoxApiClient(this._settings.VoiceVox));
                }

                // Start playing immediately - cached segments play right away, uncached segments wait
                var playbackStartTime = DateTime.UtcNow;
                ConsoleHelper.WriteInfo("Playing audio...", this._logger);
                using var audioPlayer = new AudioPlayer(this._settings.Audio);
                var fillerManager = this._settings.Filler.Enabled ? new FillerManager(this._settings.Filler, cacheManager, this._settings.VoiceVox.DefaultSpeaker) : null;
                await audioPlayer.PlayAudioSequentiallyWithGenerationAsync(processedSegments, processingChannel, fillerManager, cancellationToken);

                if (verbose)
                {
                    ConsoleHelper.WriteLine($"Audio playback completed in {(DateTime.UtcNow - playbackStartTime).TotalMilliseconds:F1}ms", this._logger);
                }
            }
            else
            {
                // Original non-cached behavior for --no-cache
                using var spinner = new ProgressSpinner("Generating speech...");
                using var apiClient = new VoiceVoxApiClient(this._settings.VoiceVox);

                await apiClient.InitializeSpeakerAsync(request.SpeakerId, cancellationToken);

                var audioQuery = await apiClient.GenerateAudioQueryAsync(request, cancellationToken);
                audioData = await apiClient.SynthesizeAudioAsync(audioQuery, request.SpeakerId, cancellationToken);

                ConsoleHelper.WriteInfo("Playing audio...", this._logger);
                using var audioPlayer = new AudioPlayer(this._settings.Audio);
                await audioPlayer.PlayAudioAsync(audioData, cancellationToken);
            }

            ConsoleHelper.WriteSuccess("Done!", this._logger);

            if (exportTask != null)
            {
                await exportTask;
            }

            if (verbose)
            {
                ConsoleHelper.WriteLine($"Total execution time: {(DateTime.UtcNow - totalStartTime).TotalMilliseconds:F1}ms", this._logger);
            }
            return 0;
        }
        catch (Exception ex)
        {
            ConsoleHelper.WriteError($"Error: {ex.Message}", this._logger);
            return 1;
        }
    }

    private async Task WriteOutputFileAsync(byte[] wavData, string outPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var extension = Path.GetExtension(outPath).ToLowerInvariant();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);

        if (extension == ".mp3")
        {
            try
            {
                var mp3Bytes = AudioConversionUtility.ConvertWavToMp3(wavData);
                // sanity-check MP3 header (0xFFEx)
                bool isMp3 = mp3Bytes.Length >= 2 && mp3Bytes[0] == 0xFF && (mp3Bytes[1] & 0xE0) == 0xE0;
                if (!isMp3)
                {
                    // fallback: write as wav with corrected extension
                    var wavOut = Path.ChangeExtension(outPath, ".wav");
                    await File.WriteAllBytesAsync(wavOut, wavData);
                    ConsoleHelper.WriteWarning($"MP3 encoding fallback to WAV: {wavOut}", this._logger);
                }
                else
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await File.WriteAllBytesAsync(outPath, mp3Bytes);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to export MP3: {ex.Message}", ex);
            }
            return;
        }

        // Default: write WAV bytes as-is
        // If extension mismatches, warn and correct
        bool isWav = wavData.Length >= 12 &&
                     wavData[0] == 'R' && wavData[1] == 'I' && wavData[2] == 'F' && wavData[3] == 'F' &&
                     wavData[8] == 'W' && wavData[9] == 'A' && wavData[10] == 'V' && wavData[11] == 'E';
        if (!isWav)
        {
            // Unexpected, but write raw bytes to requested path
            await File.WriteAllBytesAsync(outPath, wavData);
            return;
        }

        if (extension != ".wav")
        {
            var corrected = Path.ChangeExtension(outPath, ".wav");
            await File.WriteAllBytesAsync(corrected, wavData);
            ConsoleHelper.WriteWarning($"Adjusted extension to .wav: {corrected}", this._logger);
        }
        else
        {
            cancellationToken.ThrowIfCancellationRequested();
            await File.WriteAllBytesAsync(outPath, wavData);
        }
    }
}
