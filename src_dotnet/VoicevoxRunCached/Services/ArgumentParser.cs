using System.Globalization;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services;

public class ArgumentParser
{
    public static VoiceRequest? ParseArguments(string[] args, AppSettings settings)
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
                case "--log-level" when i + 1 < args.Length:
                    i++;
                    break;
                case "--log-format" when i + 1 < args.Length:
                    i++;
                    break;
                default:
                    Console.WriteLine($"Warning: Unknown option '{args[i]}'");
                    break;
            }
        }

        return request;
    }

    public static bool GetBoolOption(string[] args, string option)
    {
        return args.Contains(option);
    }

    public static string? GetStringOption(string[] args, string option)
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

    public static void ShowUsage()
    {
        Console.WriteLine("VOICEVOX REST API wrapper with caching");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  VoicevoxRunCached <text> [options]");
        Console.WriteLine("  VoicevoxRunCached --test [options]");
        Console.WriteLine("  VoicevoxRunCached speakers");
        Console.WriteLine("  VoicevoxRunCached devices [--full] [--json]");
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
        Console.WriteLine("  --log-level <level>      Log level: trace|debug|info|warn|error|crit|none (default: info; verbose=debug)");
        Console.WriteLine("  --log-format <fmt>       Log format: simple|json (default: simple)");
        Console.WriteLine("  --help, -h               Show this help message");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  speakers                 List available speakers");
        Console.WriteLine("  devices                  List audio output devices");
        Console.WriteLine("  --test                   Play the configured test message (Test.Message)");
        Console.WriteLine("  --init                   Initialize filler audio cache");
        Console.WriteLine("  --clear                  Clear all audio cache files");
        Console.WriteLine("  --benchmark              Run performance benchmarks");
    }
}
