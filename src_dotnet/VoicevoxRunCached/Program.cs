using Microsoft.Extensions.Configuration;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services;

namespace VoicevoxRunCached;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var configuration = BuildConfiguration();
        var settings = configuration.Get<AppSettings>() ?? new AppSettings();

        if (args.Length == 0 || args[0] == "--help" || args[0] == "-h")
        {
            ShowUsage();
            return 0;
        }

        if (args[0] == "speakers")
        {
            await HandleListSpeakersAsync(settings);
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

        await HandleTextToSpeechAsync(settings, request, GetBoolOption(args, "--no-cache"), GetBoolOption(args, "--cache-only"));
        return 0;
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
        Console.WriteLine("  --help, -h               Show this help message");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  speakers                 List available speakers");
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

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--speaker":
                case "-s":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int speaker))
                    {
                        request.SpeakerId = speaker;
                        i++;
                    }
                    break;
                case "--speed":
                    if (i + 1 < args.Length && double.TryParse(args[i + 1], out double speed))
                    {
                        request.Speed = speed;
                        i++;
                    }
                    break;
                case "--pitch":
                    if (i + 1 < args.Length && double.TryParse(args[i + 1], out double pitch))
                    {
                        request.Pitch = pitch;
                        i++;
                    }
                    break;
                case "--volume":
                    if (i + 1 < args.Length && double.TryParse(args[i + 1], out double volume))
                    {
                        request.Volume = volume;
                        i++;
                    }
                    break;
                case "--no-cache":
                case "--cache-only":
                case "--help":
                case "-h":
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

    private static async Task HandleTextToSpeechAsync(AppSettings settings, VoiceRequest request, bool noCache, bool cacheOnly)
    {
        try
        {
            var cacheManager = new AudioCacheManager(settings.Cache);
            byte[]? audioData = null;

            // Process text in segments for better cache efficiency
            if (!noCache)
            {
                Console.WriteLine("Processing segments...");
                var segments = await cacheManager.ProcessTextSegmentsAsync(request);
                var cachedCount = segments.Count(s => s.IsCached);
                var totalCount = segments.Count;
                
                if (cachedCount > 0)
                {
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
                        Console.WriteLine($"\e[31mError: {uncachedSegments.Count} segments not cached and --cache-only specified\e[0m"); // Red text
                        Environment.Exit(1);
                        return;
                    }

                    // C# 13 Escape character for info message
                    Console.WriteLine($"\e[33mGenerating {uncachedSegments.Count} segments in background...\e[0m"); // Yellow text
                    generationTask = GenerateSegmentsAsync(settings, request, segments, cacheManager);
                }

                // Start playing immediately - cached segments play right away, uncached segments wait
                // C# 13 Escape character for status message
                Console.WriteLine($"\\e[36mPlaying audio...\\e[0m"); // Cyan text
                using var audioPlayer = new AudioPlayer(settings.Audio);
                await audioPlayer.PlayAudioSequentiallyWithGenerationAsync(segments, generationTask);
            }
            else
            {
                // Original non-cached behavior for --no-cache
                // C# 13 Escape character for generation status
                Console.WriteLine($"\e[33mGenerating speech...\e[0m"); // Yellow text
                using var apiClient = new VoiceVoxApiClient(settings.VoiceVox);
                
                await apiClient.InitializeSpeakerAsync(request.SpeakerId);
                
                var audioQuery = await apiClient.GenerateAudioQueryAsync(request);
                audioData = await apiClient.SynthesizeAudioAsync(audioQuery, request.SpeakerId);
                
                // C# 13 Escape character for playback status
                Console.WriteLine($"\e[36mPlaying audio...\e[0m"); // Cyan text
                using var audioPlayer = new AudioPlayer(settings.Audio);
                await audioPlayer.PlayAudioAsync(audioData);
            }

            // C# 13 Escape character for completion message
            Console.WriteLine($"\e[32mDone!\e[0m"); // Green text
        }
        catch (Exception ex)
        {
            // C# 13 Escape character for error message
            Console.WriteLine($"\\e[31mError: {ex.Message}\\e[0m"); // Red text
            Environment.Exit(1);
        }
    }

    private static async Task HandleListSpeakersAsync(AppSettings settings)
    {
        try
        {
            using var apiClient = new VoiceVoxApiClient(settings.VoiceVox);
            var speakers = await apiClient.GetSpeakersAsync();

            Console.WriteLine("Available speakers:");
            foreach (var speaker in speakers)
            {
                Console.WriteLine($"  {speaker.Name} (v{speaker.Version})");
                foreach (var style in speaker.Styles)
                {
                    Console.WriteLine($"    ID: {style.Id} - {style.Name}");
                }
                Console.WriteLine();
            }
        }
        catch (Exception ex)
        {
            // C# 13 Escape character for error message
            Console.WriteLine($"\\e[31mError: {ex.Message}\\e[0m"); // Red text
            Environment.Exit(1);
        }
    }

    private static async Task GenerateSegmentsAsync(AppSettings settings, VoiceRequest request, List<TextSegment> segments, AudioCacheManager cacheManager)
    {
        using var apiClient = new VoiceVoxApiClient(settings.VoiceVox);
        await apiClient.InitializeSpeakerAsync(request.SpeakerId);

        for (int i = 0; i < segments.Count; i++)
        {
            if (!segments[i].IsCached)
            {
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
}