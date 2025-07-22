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
            Console.WriteLine("Error: Invalid arguments");
            ShowUsage();
            return 1;
        }

        await HandleTextToSpeechAsync(settings, request, GetBoolOption(args, "--no-cache"), GetBoolOption(args, "--cache-only"));
        return 0;
    }

    private static IConfiguration BuildConfiguration()
    {
        return new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
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

            if (!noCache)
            {
                Console.WriteLine("Checking cache...");
                audioData = await cacheManager.GetCachedAudioAsync(request);
                if (audioData != null)
                {
                    Console.WriteLine("Found in cache!");
                }
            }

            if (audioData == null)
            {
                if (cacheOnly)
                {
                    Console.WriteLine("Error: No cached audio found and --cache-only specified");
                    Environment.Exit(1);
                    return;
                }

                Console.WriteLine("Generating speech...");
                using var apiClient = new VoiceVoxApiClient(settings.VoiceVox);
                
                await apiClient.InitializeSpeakerAsync(request.SpeakerId);
                
                var audioQuery = await apiClient.GenerateAudioQueryAsync(request);
                audioData = await apiClient.SynthesizeAudioAsync(audioQuery, request.SpeakerId);

                if (!noCache)
                {
                    Console.WriteLine("Saving to cache...");
                    await cacheManager.SaveAudioCacheAsync(request, audioData);
                }
            }

            Console.WriteLine("Playing audio...");
            using var audioPlayer = new AudioPlayer(settings.Audio);
            await audioPlayer.PlayAudioAsync(audioData);

            Console.WriteLine("Done!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
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
            Console.WriteLine($"Error: {ex.Message}");
            Environment.Exit(1);
        }
    }
}