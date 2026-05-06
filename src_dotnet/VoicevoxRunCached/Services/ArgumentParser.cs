using System.Collections.Frozen;
using System.Globalization;
using Aloe.Utils.CommandLine;
using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services;

/// <summary>
/// テキスト読み上げコマンドのオプション
/// </summary>
public record TextToSpeechOptions(
    bool NoCache,
    bool CacheOnly,
    bool Verbose,
    string? OutPath,
    bool NoPlay);

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

    /// <summary>
    /// コマンドライン引数を前処理して、IConfigurationで使用できる形式に変換
    /// </summary>
    public static string[] PreprocessArgs(string[] args)
        => ArgsHelper.PreprocessArgs(args, FlagArgs, ShortArgs);

    /// <summary>
    /// 通常のテキスト読み上げコマンドの引数を解析する。
    /// 先頭引数をテキストとして扱い、残りをオプションとして処理する。
    /// </summary>
    /// <returns>引数が空の場合は <c>Request</c> が <c>null</c>。呼び出し側は usage 表示で対応する。</returns>
    public static (VoiceRequest? Request, TextToSpeechOptions Options) Parse(
        string[] args, AppSettings settings, ILogger? logger = null)
    {
        if (args.Length == 0)
        {
            return (null, EmptyOptions);
        }

        var processedArgs = PreprocessArgs(args);
        var request = CreateDefaultRequest(args[0], settings);
        var options = ApplyOptions(processedArgs, optionsStartIndex: 1, request, logger);

        logger?.LogDebug("引数解析結果 - Text='{Text}', SpeakerId={SpeakerId}, Speed={Speed}, Pitch={Pitch}, Volume={Volume}",
            request.Text, request.SpeakerId, request.Speed, request.Pitch, request.Volume);

        return (request, options);
    }

    /// <summary>
    /// --test コマンドの引数を解析する。
    /// テキストは事前に決まっているため呼び出し側から渡し、args は全てオプションとして処理する。
    /// </summary>
    public static (VoiceRequest Request, TextToSpeechOptions Options) ParseTestCommand(
        string text, string[] args, AppSettings settings, ILogger? logger = null)
    {
        var processedArgs = PreprocessArgs(args);
        var request = CreateDefaultRequest(text, settings);
        var options = ApplyOptions(processedArgs, optionsStartIndex: 0, request, logger);
        return (request, options);
    }

    private static readonly TextToSpeechOptions EmptyOptions = new(
        NoCache: false, CacheOnly: false, Verbose: false, OutPath: null, NoPlay: false);

    private static VoiceRequest CreateDefaultRequest(string text, AppSettings settings) => new()
    {
        Text = text,
        SpeakerId = settings.VoiceVox.DefaultSpeaker,
        Speed = 1.0,
        Pitch = 0.0,
        Volume = 1.0
    };

    /// <summary>
    /// 前処理済み引数列から音声パラメータを <see cref="VoiceRequest"/> に反映しつつ、
    /// 動作フラグを <see cref="TextToSpeechOptions"/> として返す。
    /// </summary>
    private static TextToSpeechOptions ApplyOptions(
        string[] processedArgs, int optionsStartIndex, VoiceRequest request, ILogger? logger)
    {
        bool noCache = false, cacheOnly = false, verbose = false, noPlay = false;
        string? outPath = null;

        int i = optionsStartIndex;
        while (i < processedArgs.Length)
        {
            var arg = processedArgs[i];
            switch (arg)
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
                case "--no-cache":
                    noCache = true;
                    i++;
                    break;
                case "--cache-only":
                    cacheOnly = true;
                    i++;
                    break;
                case "--verbose":
                    verbose = true;
                    i++;
                    break;
                case "--no-play":
                    noPlay = true;
                    i++;
                    break;
                case "--out" or "-o" when i + 1 < processedArgs.Length:
                    outPath = processedArgs[i + 1];
                    i += 2;
                    break;
                case "--help" or "-h":
                    i++;
                    break;
                case "--log-level" or "--log-format" when i + 1 < processedArgs.Length:
                    // ConfigurationManager.AddCommandLine 経由で LoggingSettings にバインドされる。
                    // ここでは Unknown 警告を出さないために値を読み飛ばす。
                    i += 2;
                    break;
                default:
                    if (arg.StartsWith("--out=", StringComparison.Ordinal))
                    {
                        outPath = arg["--out=".Length..];
                        i++;
                        break;
                    }
                    ConsoleHelper.WriteWarning($"Warning: Unknown option '{arg}'");
                    logger?.LogWarning("Unknown option: {Option}", arg);
                    i++;
                    break;
            }
        }

        return new TextToSpeechOptions(noCache, cacheOnly, verbose, outPath, noPlay);
    }

    public static void ShowUsage()
    {
        // ヘルプ出力は人間向けの 1 ブロックなので raw Console.Write でまとめて流す
        // (logger 経由だと毎行プレフィクスが付いてしまう)
        Console.WriteLine("""
VOICEVOX REST API wrapper with caching

Usage:
  VoicevoxRunCached <text> [options]
  VoicevoxRunCached --test [options]
  VoicevoxRunCached speakers
  VoicevoxRunCached devices [--full] [--json]
  VoicevoxRunCached --init
  VoicevoxRunCached --clear

Arguments:
  <text>                    The text to convert to speech

Options:
  --speaker, -s <id>        Speaker ID (default: 1)
  --speed <value>           Speech speed (default: 1.0)
  --pitch <value>           Speech pitch (default: 0.0)
  --volume <value>          Speech volume (default: 1.0)
  --no-cache               Skip cache usage
  --cache-only             Use cache only, don't call API
  --out, -o <path>         Save output audio to file (.wav or .mp3)
  --no-play                Do not play audio (useful with --out)
  --verbose                Show detailed timing information
  --log-level <level>      Log level: trace|debug|info|warn|error|crit|none (default: info; verbose=debug)
  --log-format <fmt>       Log format: simple|json (default: simple)
  --help, -h               Show this help message

Commands:
  speakers                 List available speakers
  devices                  List audio output devices
  --test                   Play the configured test message (Test.Message)
  --init                   Initialize filler audio cache
  --clear                  Clear all audio cache files

Enhanced Features:
  • Standalone flags: --verbose → --verbose true
  • Concatenated options: -s1 → -s 1
  • Configuration integration: Planned for future versions
  • Priority: Command-line > Environment > appsettings.json
""");
    }
}
