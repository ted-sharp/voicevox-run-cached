using System.Collections.Frozen;
using System.Globalization;
using Aloe.Utils.CommandLine;
using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services;

public static class ArgumentParser
{
    // フラグ引数の定義（--flag → --flag true）
    // S3887対応: FrozenSetを使用して不変コレクションに
    public static readonly FrozenSet<string> FlagArgs = new[]
    {
        "--verbose",
        "--no-cache",
        "--cache-only",
        "--no-play",
        "--json",
        "--full",
        "--help",
        "-h"
    }.ToFrozenSet();

    // ショート引数の定義（-uadmin → -u admin）
    // S3887対応: FrozenSetを使用して不変コレクションに
    public static readonly FrozenSet<string> ShortArgs = new[]
    {
        "-s",
        "-o"
    }.ToFrozenSet();

    // コマンドライン引数と設定プロパティのマッピング（現在は使用されていないが将来の拡張用）
    // S3887対応: FrozenDictionaryを使用して不変コレクションに
    public static readonly FrozenDictionary<string, string> Aliases = new Dictionary<string, string>
    {
        // Note: These mappings are currently not used but kept for future configuration integration
        // { "--verbose", "Logging:Verbose" },
        // { "--no-cache", "Cache:Disabled" },
        // { "--cache-only", "Cache:Only" },
        // { "--no-play", "Audio:NoPlay" },
        // { "--json", "Output:Json" },
        // { "--full", "Output:Full" },
        // { "-s", "VoiceVox:SpeakerId" },
        // { "-o", "Output:Path" }
    }.ToFrozenDictionary();

    /// <summary>
    /// コマンドライン引数を前処理して、IConfigurationで使用できる形式に変換
    /// </summary>
    public static string[] PreprocessArgs(string[] args)
    {
        return ArgsHelper.PreprocessArgs(args, FlagArgs, ShortArgs);
    }

    /// <summary>
    /// 前処理された引数からVoiceRequestを解析
    /// </summary>
    public static VoiceRequest? ParseArguments(string[] args, AppSettings settings, ILogger? logger = null)
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

        // デバッグ用のログ出力
        logger?.LogDebug("引数解析結果 - Text='{Text}', SpeakerId={SpeakerId}, Speed={Speed}, Pitch={Pitch}, Volume={Volume}",
            request.Text, request.SpeakerId, request.Speed, request.Pitch, request.Volume);

        // 前処理された引数を解析
        var processedArgs = PreprocessArgs(args);

        // C# 13 Enhanced pattern matching for cleaner argument parsing
        int i = 1;
        while (i < processedArgs.Length)
        {
            switch (processedArgs[i])
            {
                case "--speaker" or "-s" when i + 1 < processedArgs.Length && Int32.TryParse(processedArgs[i + 1], out int speaker):
                    request.SpeakerId = speaker;
                    i += 2;
                    break;
                case "--speed" when i + 1 < processedArgs.Length && Double.TryParse(processedArgs[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double speed):
                    request.Speed = speed;
                    i += 2;
                    break;
                case "--pitch" when i + 1 < processedArgs.Length && Double.TryParse(processedArgs[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double pitch):
                    request.Pitch = pitch;
                    i += 2;
                    break;
                case "--volume" when i + 1 < processedArgs.Length && Double.TryParse(processedArgs[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double volume):
                    request.Volume = volume;
                    i += 2;
                    break;
                case "--no-cache" or "--cache-only" or "--verbose" or "--help" or "-h" or "--no-play":
                    i++;
                    break;
                case "--out" or "-o" when i + 1 < processedArgs.Length:
                    i += 2;
                    break;
                case "--log-level" when i + 1 < processedArgs.Length:
                    i += 2;
                    break;
                case "--log-format" when i + 1 < processedArgs.Length:
                    i += 2;
                    break;
                default:
                    Console.WriteLine($"Warning: Unknown option '{processedArgs[i]}'");
                    i++;
                    break;
            }
        }

        return request;
    }

    /// <summary>
    /// 前処理された引数からブール値を取得
    /// </summary>
    public static bool GetBoolOption(string[] args, string option)
    {
        var processedArgs = PreprocessArgs(args);
        return processedArgs.Contains(option);
    }

    /// <summary>
    /// 前処理された引数から文字列値を取得
    /// </summary>
    public static string? GetStringOption(string[] args, string option)
    {
        var processedArgs = PreprocessArgs(args);
        for (int i = 0; i < processedArgs.Length - 1; i++)
        {
            if (processedArgs[i] == option)
            {
                return processedArgs[i + 1];
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
        Console.WriteLine();
        Console.WriteLine("Enhanced Features:");
        Console.WriteLine("  • Standalone flags: --verbose → --verbose true");
        Console.WriteLine("  • Concatenated options: -s1 → -s 1");
        Console.WriteLine("  • Configuration integration: Planned for future versions");
        Console.WriteLine("  • Priority: Command-line > Environment > appsettings.json");
    }
}
