using System.Collections.Frozen;
using System.Globalization;
using Aloe.Utils.CommandLine;
using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Exceptions;
using VoicevoxRunCached.Models;

namespace VoicevoxRunCached.Services;

/// <summary>
/// テキスト読み上げコマンドのオプション
/// </summary>
/// <param name="NoCache">キャッシュを読みも書きもしない</param>
/// <param name="Refresh">キャッシュを読まずに全部合成し直し、キャッシュを上書きする (辞書登録・エンジン更新の反映用)</param>
public record TextToSpeechOptions(
    bool NoCache,
    bool CacheOnly,
    bool Verbose,
    string? OutPath,
    bool NoPlay,
    bool Refresh = false);

public static class ArgumentParser
{
    // フラグ引数の定義（--flag → --flag true）
    // 値を取らないコマンド (--init 等) も含める。含めないと IConfiguration の読み込みで直後の引数が
    // そのコマンドの値として消費され、--init --log-level debug のログレベルが効かない
    // S3887対応: FrozenSetを使用して不変コレクションに
    public static readonly FrozenSet<string> FlagArgs = new[]
    {
        "--init",
        "--clear",
        "--test",
        "--verbose",
        "--no-cache",
        "--cache-only",
        "--refresh",
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

        // 本文 (args[0]) は前処理しない。"-sample" のような先頭ハイフンの本文が "-s ample" に割られるのを防ぐ
        string[] processedArgs = [args[0], .. PreprocessArgs(args[1..])];
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

    /// <summary>
    /// <c>--init</c> の引数から、フィラーを生成する話者 (<c>--speaker</c> / <c>-s</c>) を読む。省略時は <c>DefaultSpeaker</c>。
    /// </summary>
    /// <remarks>
    /// フィラーは読み上げと同じ話者の声で再生するので、既定以外の話者で読み上げるならその話者の分を生成しておく。
    /// それ以外の引数 (<c>--log-level</c> や <c>--Section:Key=値</c>) は設定の読み込み側で扱うので、ここでは読み飛ばす。
    /// </remarks>
    public static int ParseInitSpeaker(string[] args, AppSettings settings)
    {
        var processedArgs = PreprocessArgs(args);
        var speakerId = settings.VoiceVox.DefaultSpeaker;
        for (int i = 0; i < processedArgs.Length; i++)
        {
            if (processedArgs[i] is "--speaker" or "-s")
            {
                speakerId = ReadInt(processedArgs, i, min: 0);
                i++;
            }
        }
        return speakerId;
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
        bool noCache = false, cacheOnly = false, verbose = false, noPlay = false, refresh = false;
        string? outPath = null;

        int i = optionsStartIndex;
        while (i < processedArgs.Length)
        {
            var arg = processedArgs[i];
            switch (arg)
            {
                case "--speaker" or "-s":
                    request.SpeakerId = ReadInt(processedArgs, i, min: 0);
                    i += 2;
                    break;
                case "--speed":
                    request.Speed = ReadDouble(processedArgs, i, min: 0, minExclusive: true);
                    i += 2;
                    break;
                case "--pitch":
                    request.Pitch = ReadDouble(processedArgs, i);
                    i += 2;
                    break;
                case "--volume":
                    request.Volume = ReadDouble(processedArgs, i, min: 0);
                    i += 2;
                    break;
                case "--no-cache":
                    noCache = ReadFlag(processedArgs, ref i);
                    break;
                case "--cache-only":
                    cacheOnly = ReadFlag(processedArgs, ref i);
                    break;
                case "--refresh":
                    refresh = ReadFlag(processedArgs, ref i);
                    break;
                case "--verbose":
                    verbose = ReadFlag(processedArgs, ref i);
                    break;
                case "--no-play":
                    noPlay = ReadFlag(processedArgs, ref i);
                    break;
                case "--out" or "-o":
                    outPath = ValidateOutPath(arg, ReadValue(processedArgs, i));
                    i += 2;
                    break;
                case "--help" or "-h":
                    ReadFlag(processedArgs, ref i);
                    break;
                case "--log-level" or "--log-format" when i + 1 < processedArgs.Length:
                    // ConfigurationManager.SwitchMappings で Logging:Level / Logging:Format にバインドされる。
                    // ここでは Unknown 警告を出さないために値を読み飛ばす。
                    i += 2;
                    break;
                default:
                    if (arg.StartsWith("--out=", StringComparison.Ordinal))
                    {
                        outPath = ValidateOutPath("--out", arg["--out=".Length..]);
                        i++;
                        break;
                    }
                    if (arg.StartsWith("--log-level=", StringComparison.Ordinal) || arg.StartsWith("--log-format=", StringComparison.Ordinal))
                    {
                        // 値は ConfigurationManager.SwitchMappings 経由で反映済み
                        i++;
                        break;
                    }
                    ConsoleHelper.WriteWarning($"Warning: Unknown option '{arg}'");
                    logger?.LogWarning("Unknown option: {Option}", arg);
                    i++;
                    break;
            }
        }

        if (refresh && (noCache || cacheOnly))
        {
            // --refresh は「合成し直してキャッシュを上書きする」なので、キャッシュを書かない --no-cache・
            // 合成しない --cache-only とは両立しない。どちらかを黙って優先すると意図と違う動きになる
            throw InvalidOption("--refresh", "--no-cache / --cache-only と同時には指定できません。");
        }

        return new TextToSpeechOptions(noCache, cacheOnly, verbose, outPath, noPlay, refresh);
    }

    /// <summary>
    /// フラグ引数を読み、<paramref name="i"/> を次の引数位置へ進める。
    /// </summary>
    /// <remarks>
    /// <see cref="PreprocessArgs"/> は IConfiguration 向けに <c>--flag</c> を <c>--flag true</c> へ展開するため、
    /// 直後の <c>true</c>/<c>false</c> は値として消費する (読み飛ばさないと "Unknown option 'true'" 警告になる)。
    /// </remarks>
    private static bool ReadFlag(string[] args, ref int i)
    {
        if (i + 1 < args.Length && Boolean.TryParse(args[i + 1], out var value))
        {
            i += 2;
            return value;
        }
        i++;
        return true;
    }

    /// <summary>
    /// <paramref name="optionIndex"/> 位置のオプションに続く値を返す。値が無ければ例外。
    /// </summary>
    /// <remarks>
    /// 不正値をデフォルトで黙って続行すると、意図と違うパラメータで合成・キャッシュされるためエラーで止める。
    /// 次が <c>--xxx</c> の場合も値の指定漏れとみなす (<c>--out --no-play</c> で "--no-play" というファイルを作らない)。
    /// 負数 (<c>-0.1</c>) は値として受け付けるため、単一ハイフンは対象外。
    /// </remarks>
    private static string ReadValue(string[] args, int optionIndex)
    {
        if (optionIndex + 1 >= args.Length || args[optionIndex + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw InvalidOption(args[optionIndex], "値が指定されていません。");
        }
        return args[optionIndex + 1];
    }

    /// <summary>
    /// <c>--out</c> の拡張子を検証する。合成してから失敗しないよう、解析時点で弾く。
    /// </summary>
    private static string ValidateOutPath(string option, string path)
    {
        if (!AudioExportService.SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
        {
            throw InvalidOption(option,
                $"対応していない拡張子です: '{path}' ({String.Join(" / ", AudioExportService.SupportedExtensions)} のみ)");
        }
        return path;
    }

    private static int ReadInt(string[] args, int optionIndex, int min)
    {
        var raw = ReadValue(args, optionIndex);
        if (!Int32.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw InvalidOption(args[optionIndex], $"整数ではありません: '{raw}'");
        }
        if (value < min)
        {
            throw InvalidOption(args[optionIndex], $"{min} 以上である必要があります: {value}");
        }
        return value;
    }

    /// <remarks>
    /// 範囲判定はキャッシュキー / API と同じ粒度に丸めた値で行う
    /// (<c>--speed 0.004</c> は丸めると 0 になり、生値の "&gt; 0" 判定をすり抜けるため)。
    /// </remarks>
    private static double ReadDouble(string[] args, int optionIndex, double min = Double.NegativeInfinity, bool minExclusive = false)
    {
        var raw = ReadValue(args, optionIndex);
        if (!Double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !Double.IsFinite(value))
        {
            throw InvalidOption(args[optionIndex], $"数値ではありません: '{raw}'");
        }
        var normalized = Cache.CacheKeyGenerator.NormalizeParameter(value);
        if (minExclusive ? normalized <= min : normalized < min)
        {
            var bound = minExclusive ? $"{min} より大きい" : $"{min} 以上である";
            throw InvalidOption(args[optionIndex], $"{bound}必要があります: {raw}");
        }
        return value;
    }

    private static VoicevoxRunCachedException InvalidOption(string option, string reason) => new(
        errorCode: ErrorCodes.General.InvalidArguments,
        message: $"Invalid value for {option}: {reason}",
        userMessage: $"オプション {option} の指定が不正です。{reason}",
        suggestedSolution: "--help で使い方を確認してください。");

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
  VoicevoxRunCached --init [--speaker <id>]
  VoicevoxRunCached --clear

Arguments:
  <text>                    The text to convert to speech

Options:
  --speaker, -s <id>       Speaker ID (default: appsettings VoiceVox.DefaultSpeaker)
  --speed <value>          Speech speed (default: 1.0)
  --pitch <value>          Speech pitch (default: 0.0)
  --volume <value>         Speech volume (default: 1.0)
  --no-cache               Skip cache usage (neither read nor write)
  --cache-only             Use cache only, don't call API
  --refresh                Re-synthesize all segments and overwrite the cache
  --out, -o <path>         Save output audio to file (.wav or .opus)
  --no-play                Do not play audio (useful with --out)
  --verbose                Show playback time and total execution time
  --log-level <level>      Log level: trace|debug|info|warn|error|crit|none (default: appsettings Logging.Level)
  --log-format <fmt>       Log format: simple|json (default: appsettings Logging.Format)
  --help, -h               Show this help message

Commands:
  speakers                 List available speakers
  devices                  List audio output devices
  --test                   Play the configured test message (Test.Message)
  --init                   Initialize filler audio cache for the speaker (default: VoiceVox.DefaultSpeaker)
  --clear                  Clear audio and filler cache files

Enhanced Features:
  - Standalone flags: --verbose → --verbose true
  - Concatenated options: -s1 → -s 1
  - Priority: Command-line > appsettings.Development.json > appsettings.json
""");
    }
}
