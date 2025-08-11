using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Runtime.InteropServices;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services;
using NAudio.Wave;
using NAudio.MediaFoundation;

namespace VoicevoxRunCached;

class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    private const int STD_OUTPUT_HANDLE = -11;
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

    static async Task<int> Main(string[] args)
    {
        EnableAnsiColors();

        // Initialize Media Foundation once per process
        MediaFoundationManager.Initialize();

        var configuration = BuildConfiguration();
        var settings = configuration.Get<AppSettings>() ?? new AppSettings();

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder
                .SetMinimumLevel(LogLevel.Information)
                .AddConsole(options =>
                {
                    options.TimestampFormat = "HH:mm:ss.fff ";
                    options.IncludeScopes = false;
                });
        });
        var logger = loggerFactory.CreateLogger("VoicevoxRunCached");

        if (args.Length == 0 || args[0] == "--help" || args[0] == "-h")
        {
            ShowUsage();
            return 0;
        }

        if (args[0] == "speakers")
        {
            await HandleListSpeakersAsync(settings, logger);
            return 0;
        }

        if (args[0] == "devices")
        {
            HandleListDevices(logger);
            return 0;
        }

        if (args[0] == "--init")
        {
            await HandleInitializeFillerAsync(settings, logger);
            return 0;
        }

        if (args[0] == "--clear")
        {
            await HandleClearCacheAsync(settings, logger);
            return 0;
        }

        var request = ParseArguments(args, settings);
        if (request == null)
        {
            // C# 13 Escape character improvement: \e for ESCAPE
            Console.WriteLine($"\e[31mError: Invalid arguments\e[0m"); // Red text
            ShowUsage();
            return 1;
        }

        string? outPath = GetStringOption(args, "--out") ?? GetStringOption(args, "-o");
        bool noPlay = GetBoolOption(args, "--no-play");

        await HandleTextToSpeechAsync(settings, request, GetBoolOption(args, "--no-cache"), GetBoolOption(args, "--cache-only"), GetBoolOption(args, "--verbose"), outPath, noPlay, logger);
        return 0;
    }

    private static void EnableAnsiColors()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var handle = GetStdHandle(STD_OUTPUT_HANDLE);
            if (GetConsoleMode(handle, out uint mode))
            {
                mode |= ENABLE_VIRTUAL_TERMINAL_PROCESSING;
                SetConsoleMode(handle, mode);
            }
        }
    }

    private static IConfiguration BuildConfiguration()
    {
        // Use the directory where the executable is located, not the current working directory
        var executablePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
        var executableDirectory = Path.GetDirectoryName(executablePath) ?? Directory.GetCurrentDirectory();

        return new ConfigurationBuilder()
            .SetBasePath(executableDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();
    }

    private static void ShowUsage()
    {
        Console.WriteLine("VOICEVOX REST API wrapper with caching");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  VoicevoxRunCached <text> [options]");
        Console.WriteLine("  VoicevoxRunCached speakers");
        Console.WriteLine("  VoicevoxRunCached devices");
        Console.WriteLine("  VoicevoxRunCached --init");
        Console.WriteLine("  VoicevoxRunCached --clear");
        Console.WriteLine();
        Console.WriteLine("Arguments:");
        Console.WriteLine("  <text>                    The text to convert to speech");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --speaker, -s <id>        Speaker ID (default: 1)");
        Console.WriteLine("  --speed <value>           Speech speed (default: 1.0)");
        Console.WriteLine("  --pitch <value>           Speech pitch (default: 0.0)");
        Console.WriteLine("  --volume <value>          Speech volume (default: 1.0)");
        Console.WriteLine("  --no-cache               Skip cache usage");
        Console.WriteLine("  --cache-only             Use cache only, don't call API");
        Console.WriteLine("  --out, -o <path>         Save output audio to file (.wav or .mp3)");
        Console.WriteLine("  --no-play                Do not play audio (useful with --out)");
        Console.WriteLine("  --verbose                Show detailed timing information");
        Console.WriteLine("  --help, -h               Show this help message");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  speakers                 List available speakers");
        Console.WriteLine("  devices                  List audio output devices");
        Console.WriteLine("  --init                   Initialize filler audio cache");
        Console.WriteLine("  --clear                  Clear all audio cache files");
    }

    private static VoiceRequest? ParseArguments(string[] args, AppSettings settings)
    {
        if (args.Length == 0)
            return null;

        var request = new VoiceRequest
        {
            Text = args[0],
            SpeakerId = settings.VoiceVox.DefaultSpeaker,
            Speed = 1.0,
            Pitch = 0.0,
            Volume = 1.0
        };

        // C# 13 Enhanced pattern matching for cleaner argument parsing
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--speaker" or "-s" when i + 1 < args.Length && Int32.TryParse(args[i + 1], out int speaker):
                    request.SpeakerId = speaker;
                    i++;
                    break;
                case "--speed" when i + 1 < args.Length && Double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double speed):
                    request.Speed = speed;
                    i++;
                    break;
                case "--pitch" when i + 1 < args.Length && Double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double pitch):
                    request.Pitch = pitch;
                    i++;
                    break;
                case "--volume" when i + 1 < args.Length && Double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double volume):
                    request.Volume = volume;
                    i++;
                    break;
                case "--no-cache" or "--cache-only" or "--verbose" or "--help" or "-h" or "--no-play":
                    break;
                case "--out" or "-o" when i + 1 < args.Length:
                    i++;
                    break;
                default:
                    Console.WriteLine($"Warning: Unknown option '{args[i]}'");
                    break;
            }
        }

        return request;
    }

    private static bool GetBoolOption(string[] args, string option)
    {
        return args.Contains(option);
    }

    private static string? GetStringOption(string[] args, string option)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == option)
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static async Task HandleTextToSpeechAsync(AppSettings settings, VoiceRequest request, bool noCache, bool cacheOnly, bool verbose = false, string? outPath = null, bool noPlay = false, ILogger? logger = null)
    {
        var totalStartTime = DateTime.UtcNow;
        try
        {
            // Ensure VOICEVOX engine is running
            var engineStartTime = DateTime.UtcNow;
            using var engineManager = new VoiceVoxEngineManager(settings.VoiceVox);
            if (!await engineManager.EnsureEngineRunningAsync())
            {
                logger?.LogError("VOICEVOX engine is not available");
                Console.WriteLine("\e[31mError: VOICEVOX engine is not available\e[0m");
                Environment.Exit(1);
                return;
            }

            if (verbose)
            {
                logger?.LogInformation("Engine check completed in {ElapsedMs}ms", (DateTime.UtcNow - engineStartTime).TotalMilliseconds);
                Console.WriteLine($"Engine check completed in {(DateTime.UtcNow - engineStartTime).TotalMilliseconds:F1}ms");
            }

            // If output file specified, start background export task (single-shot full text generation)
            Task? exportTask = null;
            if (!String.IsNullOrWhiteSpace(outPath))
            {
                exportTask = Task.Run(async () =>
                {
                    try
                    {
                        using var apiClient = new VoiceVoxApiClient(settings.VoiceVox);
                        await apiClient.InitializeSpeakerAsync(request.SpeakerId);
                        var audioQuery = await apiClient.GenerateAudioQueryAsync(request);
                        var wavData = await apiClient.SynthesizeAudioAsync(audioQuery, request.SpeakerId);
                        await WriteOutputFileAsync(wavData, outPath!);
                        logger?.LogInformation("Saved output to: {OutPath}", outPath);
                        Console.WriteLine($"\e[32mSaved output to: {outPath}\e[0m");
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "Failed to save output to {OutPath}", outPath);
                        Console.WriteLine($"Warning: Failed to save output to '{outPath}': {ex.Message}");
                    }
                });
            }

            if (noPlay)
            {
                if (exportTask != null)
                {
                    await exportTask;
                }
                logger?.LogInformation("Done (no-play mode)");
                Console.WriteLine("\e[32mDone!\e[0m");
                if (verbose)
                {
                    logger?.LogInformation("Total execution time: {ElapsedMs}ms", (DateTime.UtcNow - totalStartTime).TotalMilliseconds);
                    Console.WriteLine($"Total execution time: {(DateTime.UtcNow - totalStartTime).TotalMilliseconds:F1}ms");
                }
                return;
            }

            var cacheManager = new AudioCacheManager(settings.Cache);
            byte[]? audioData = null;

            // Process text in segments for better cache efficiency
            if (!noCache)
            {
                var segmentStartTime = DateTime.UtcNow;
                logger?.LogInformation("Processing segments...");
                Console.WriteLine("Processing segments...");
                var segments = await cacheManager.ProcessTextSegmentsAsync(request);

                if (verbose)
                {
                    logger?.LogInformation("Segment processing completed in {ElapsedMs}ms", (DateTime.UtcNow - segmentStartTime).TotalMilliseconds);
                    Console.WriteLine($"Segment processing completed in {(DateTime.UtcNow - segmentStartTime).TotalMilliseconds:F1}ms");
                }
                var cachedCount = segments.Count(s => s.IsCached);
                var totalCount = segments.Count;

                if (cachedCount > 0)
                {
                    logger?.LogInformation("Found {Cached}/{Total} segments in cache", cachedCount, totalCount);
                    // C# 13 Escape character for success message
                    Console.WriteLine($"\e[32mFound {cachedCount}/{totalCount} segments in cache!\e[0m"); // Green text
                }

                var uncachedSegments = segments.Where(s => !s.IsCached).ToList();

                // Start background generation for uncached segments
                Task? generationTask = null;
                if (uncachedSegments.Count > 0)
                {
                    if (cacheOnly)
                    {
                        // C# 13 Escape character for error message
                        logger?.LogError("{Count} segments not cached and --cache-only specified", uncachedSegments.Count);
                        Console.WriteLine($"\e[31mError: {uncachedSegments.Count} segments not cached and --cache-only specified\e[0m"); // Red text
                        Environment.Exit(1);
                        return;
                    }

                    logger?.LogInformation("Generating {Count} segments in background...", uncachedSegments.Count);
                    // C# 13 Escape character for info message
                    Console.WriteLine($"\e[33mGenerating {uncachedSegments.Count} segments in background...\e[0m"); // Yellow text
                    generationTask = GenerateSegmentsAsync(settings, request, segments, cacheManager);
                }

                // Start playing immediately - cached segments play right away, uncached segments wait
                // C# 13 Escape character for status message
                var playbackStartTime = DateTime.UtcNow;
                logger?.LogInformation("Playing audio...");
                Console.WriteLine($"\e[36mPlaying audio...\e[0m"); // Cyan text
                using var audioPlayer = new AudioPlayer(settings.Audio);
                var fillerManager = settings.Filler.Enabled ? new FillerManager(settings.Filler, cacheManager, settings.VoiceVox.DefaultSpeaker) : null;
                await audioPlayer.PlayAudioSequentiallyWithGenerationAsync(segments, generationTask, fillerManager);

                if (verbose)
                {
                    logger?.LogInformation("Audio playback completed in {ElapsedMs}ms", (DateTime.UtcNow - playbackStartTime).TotalMilliseconds);
                    Console.WriteLine($"Audio playback completed in {(DateTime.UtcNow - playbackStartTime).TotalMilliseconds:F1}ms");
                }
            }
            else
            {
                // Original non-cached behavior for --no-cache
                using var spinner = new ProgressSpinner("Generating speech...");
                using var apiClient = new VoiceVoxApiClient(settings.VoiceVox);

                await apiClient.InitializeSpeakerAsync(request.SpeakerId);

                var audioQuery = await apiClient.GenerateAudioQueryAsync(request);
                audioData = await apiClient.SynthesizeAudioAsync(audioQuery, request.SpeakerId);

                // disposed by using

                // C# 13 Escape character for playback status
                logger?.LogInformation("Playing audio...");
                Console.WriteLine($"\e[36mPlaying audio...\e[0m"); // Cyan text
                using var audioPlayer = new AudioPlayer(settings.Audio);
                await audioPlayer.PlayAudioAsync(audioData);
            }

            logger?.LogInformation("Done");
            // C# 13 Escape character for completion message
            Console.WriteLine($"\e[32mDone!\e[0m"); // Green text

            if (exportTask != null)
            {
                await exportTask;
            }

            if (verbose)
            {
                logger?.LogInformation("Total execution time: {ElapsedMs}ms", (DateTime.UtcNow - totalStartTime).TotalMilliseconds);
                Console.WriteLine($"Total execution time: {(DateTime.UtcNow - totalStartTime).TotalMilliseconds:F1}ms");
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Unhandled error");
            // C# 13 Escape character for error message
            Console.WriteLine($"\e[31mError: {ex.Message}\e[0m"); // Red text
            Environment.Exit(1);
        }
    }

    private static async Task HandleListSpeakersAsync(AppSettings settings, ILogger logger)
    {
        try
        {
            // Ensure VOICEVOX engine is running
            using var engineManager = new VoiceVoxEngineManager(settings.VoiceVox);
            if (!await engineManager.EnsureEngineRunningAsync())
            {
                logger.LogError("VOICEVOX engine is not available");
                Console.WriteLine("\e[31mError: VOICEVOX engine is not available\e[0m");
                Environment.Exit(1);
                return;
            }

            using var apiClient = new VoiceVoxApiClient(settings.VoiceVox);
            var speakers = await apiClient.GetSpeakersAsync();

            logger.LogInformation("Available speakers:");
            Console.WriteLine("Available speakers:");
            foreach (var speaker in speakers)
            {
                logger.LogInformation("{Name} (v{Version})", speaker.Name, speaker.Version);
                Console.WriteLine($"  {speaker.Name} (v{speaker.Version})");
                foreach (var style in speaker.Styles)
                {
                    logger.LogInformation("  ID: {Id} - {Name}", style.Id, style.Name);
                    Console.WriteLine($"    ID: {style.Id} - {style.Name}");
                }
                Console.WriteLine();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error listing speakers");
            // C# 13 Escape character for error message
            Console.WriteLine($"\e[31mError: {ex.Message}\e[0m"); // Red text
            Environment.Exit(1);
        }
    }

    private static async Task GenerateSegmentsAsync(AppSettings settings, VoiceRequest request, List<TextSegment> segments, AudioCacheManager cacheManager)
    {
        using var spinner = new ProgressSpinner($"Generating segment 1/{segments.Count(s => !s.IsCached)}");
        using var apiClient = new VoiceVoxApiClient(settings.VoiceVox);
        await apiClient.InitializeSpeakerAsync(request.SpeakerId);

        int uncachedCount = 0;
        int totalUncached = segments.Count(s => !s.IsCached);

        for (int i = 0; i < segments.Count; i++)
        {
            if (!segments[i].IsCached)
            {
                uncachedCount++;
                spinner.UpdateMessage($"Generating segment {uncachedCount}/{totalUncached}");
                var segmentRequest = new VoiceRequest
                {
                    Text = segments[i].Text,
                    SpeakerId = request.SpeakerId,
                    Speed = request.Speed,
                    Pitch = request.Pitch,
                    Volume = request.Volume
                };

                var audioQuery = await apiClient.GenerateAudioQueryAsync(segmentRequest);
                var segmentAudio = await apiClient.SynthesizeAudioAsync(audioQuery, segmentRequest.SpeakerId);

                segments[i].AudioData = segmentAudio;
                segments[i].IsCached = true; // Mark as ready for playback

                // Cache the segment
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await cacheManager.SaveAudioCacheAsync(segmentRequest, segmentAudio);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Warning: Failed to cache segment: {ex.Message}");
                    }
                });
            }
        }
    }

    private static async Task HandleInitializeFillerAsync(AppSettings settings, ILogger logger)
    {
        try
        {
            // Ensure VOICEVOX engine is running
            using var engineManager = new VoiceVoxEngineManager(settings.VoiceVox);
            if (!await engineManager.EnsureEngineRunningAsync())
            {
                logger.LogError("VOICEVOX engine is not available");
                Console.WriteLine("\e[31mError: VOICEVOX engine is not available\e[0m");
                Environment.Exit(1);
                return;
            }

            var cacheManager = new AudioCacheManager(settings.Cache);
            var fillerManager = new FillerManager(settings.Filler, cacheManager, settings.VoiceVox.DefaultSpeaker);

            logger.LogInformation("Initializing filler cache...");
            await fillerManager.InitializeFillerCacheAsync(settings);
            logger.LogInformation("Filler cache initialized");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error initializing filler cache");
            Console.WriteLine($"\e[31mError initializing filler cache: {ex.Message}\e[0m");
            Environment.Exit(1);
        }
    }

    private static async Task HandleClearCacheAsync(AppSettings settings, ILogger logger)
    {
        try
        {
            using var spinner = new ProgressSpinner("Clearing audio cache...");
            var cacheManager = new AudioCacheManager(settings.Cache);

            await cacheManager.ClearAllCacheAsync();

            // Also clear filler cache using configured filler directory
            var fillerManager = new FillerManager(settings.Filler, cacheManager, settings.VoiceVox.DefaultSpeaker);
            await fillerManager.ClearFillerCacheAsync();

            logger.LogInformation("Cache cleared successfully");
            // disposed by using
            Console.WriteLine("\e[32mCache cleared successfully!\e[0m"); // Green text
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error clearing cache");
            Console.WriteLine($"\e[31mError clearing cache: {ex.Message}\e[0m"); // Red text
            Environment.Exit(1);
        }
    }

    private static void HandleListDevices(ILogger logger)
    {
        try
        {
            logger.LogInformation("Available output devices:");
            Console.WriteLine("Available output devices:");
            Console.WriteLine("  -1: Default Device");
            // Detailed enumeration is intentionally omitted for stability across environments
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error listing devices");
            Console.WriteLine($"\e[31mError listing devices: {ex.Message}\e[0m");
            Environment.Exit(1);
        }
    }

    private static async Task WriteOutputFileAsync(byte[] wavData, string outPath)
    {
        var extension = Path.GetExtension(outPath).ToLowerInvariant();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);

        if (extension == ".mp3")
        {
            try
            {
                using var wavStream = new MemoryStream(wavData);
                using var reader = new WaveFileReader(wavStream);
                using var outputStream = new MemoryStream();
                MediaFoundationManager.EnsureInitialized();
                MediaFoundationEncoder.EncodeToMp3(reader, outputStream, 128000);
                await File.WriteAllBytesAsync(outPath, outputStream.ToArray());
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to export MP3: {ex.Message}", ex);
            }
            return;
        }

        // Default: write WAV bytes as-is
        await File.WriteAllBytesAsync(outPath, wavData);
    }
}
