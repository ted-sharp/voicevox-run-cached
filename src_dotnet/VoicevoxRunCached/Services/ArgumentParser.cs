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
    /// 引数の先頭をテキスト、残りを音声オプションとして解析する。
    /// </summary>
    public static VoiceRequest? ParseArguments(string[] args, AppSettings settings, ILogger? logger = null)
    {
        if (args.Length == 0)
            return null;

        var request = CreateDefaultRequest(args[0], settings);
        ApplyVoiceOptions(args, optionsStartIndex: 1, request, logger);

        logger?.LogDebug("引数解析結果 - Text='{Text}', SpeakerId={SpeakerId}, Speed={Speed}, Pitch={Pitch}, Volume={Volume}",
            request.Text, request.SpeakerId, request.Speed, request.Pitch, request.Volume);

        return request;
    }

    /// <summary>
    /// 既知のテキストと残りのオプション引数から VoiceRequest を構築する。
    /// --test のように先頭引数がテキストでないコマンドから使用する。
    /// </summary>
    public static VoiceRequest BuildRequestFromOptions(string text, string[] optionArgs, AppSettings settings, ILogger? logger = null)
    {
        var request = CreateDefaultRequest(text, settings);
        ApplyVoiceOptions(optionArgs, optionsStartIndex: 0, request, logger);
        return request;
    }

    private static VoiceRequest CreateDefaultRequest(string text, AppSettings settings) => new()
    {
        Text = text,
        SpeakerId = settings.VoiceVox.DefaultSpeaker,
        Speed = 1.0,
        Pitch = 0.0,
        Volume = 1.0
    };

    /// <summary>
    /// --speaker/--speed/--pitch/--volume を VoiceRequest に反映する。それ以外のフラグは読み飛ばす。
    /// </summary>
    private static void ApplyVoiceOptions(string[] args, int optionsStartIndex, VoiceRequest request, ILogger? logger)
    {
        var processedArgs = PreprocessArgs(args);

        int i = optionsStartIndex;
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
                case "--log-level" or "--log-format" when i + 1 < processedArgs.Length:
                    // ConfigurationManager.AddCommandLine 経由で LoggingSettings にバインドされる。
                    // ここでは Unknown 警告を出さないために値を読み飛ばすだけ。
                    i += 2;
                    break;
                default:
                    ConsoleHelper.WriteWarning($"Warning: Unknown option '{processedArgs[i]}'");
                    logger?.LogWarning("Unknown option: {Option}", processedArgs[i]);
                    i++;
                    break;
            }
        }
    }

    /// <summary>
    /// 前処理済み引数から指定オプションの直後の値を取得 (-o foo / --out foo の形式)。
    /// </summary>
    private static string? GetStringOption(string[] processedArgs, string option)
    {
        for (int i = 0; i < processedArgs.Length - 1; i++)
        {
            if (processedArgs[i] == option)
            {
                return processedArgs[i + 1];
            }
        }
        return null;
    }

    /// <summary>
    /// テキスト読み上げコマンドのオプションを解析します
    /// </summary>
    /// <param name="args">コマンドライン引数</param>
    /// <returns>解析されたオプション</returns>
    public static TextToSpeechOptions ParseTextToSpeechOptions(string[] args)
    {
        // PreprocessArgs は flag → "--flag true" 等の正規化を行う重い処理なので 1 回だけ走らせる
        var processedArgs = PreprocessArgs(args);

        // --out=foo の形式と --out foo / -o foo の形式の両方をサポート
        var outPath = processedArgs.FirstOrDefault(arg => arg.StartsWith("--out="))?.Substring("--out=".Length)
            ?? GetStringOption(processedArgs, "--out")
            ?? GetStringOption(processedArgs, "-o");

        return new TextToSpeechOptions(
            NoCache: processedArgs.Contains("--no-cache"),
            CacheOnly: processedArgs.Contains("--cache-only"),
            Verbose: processedArgs.Contains("--verbose"),
            OutPath: outPath,
            NoPlay: processedArgs.Contains("--no-play")
        );
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
