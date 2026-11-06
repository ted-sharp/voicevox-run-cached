using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Exceptions;

namespace VoicevoxRunCached.Services.Infrastructure;

/// <summary>
/// アプリケーションの初期化と設定を行うクラス
/// </summary>
public static class ApplicationBootstrap
{
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    /// <summary>
    /// アプリケーションの初期化を実行します
    /// </summary>
    /// <param name="args">コマンドライン引数</param>
    /// <returns>初期化された設定とロガー</returns>
    public static async Task<(AppSettings settings, Microsoft.Extensions.Logging.ILogger logger)> InitializeAsync(string[] args)
    {
        // 何か出力する前に、リダイレクト先の文字コードを UTF-8 にする
        ConfigureRedirectedOutputEncoding();

        // ANSIカラーを有効化
        EnableAnsiColors();

        // 設定を構築
        var settings = LoadSettings(args);

        // 設定を検証
        ValidateConfiguration(settings);

        // ログを設定
        var loggingManager = new LoggingManager(settings.Logging);
        loggingManager.ConfigureSerilog();

        // ロガーファクトリを作成
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(Log.Logger, dispose: false);
        });
        var logger = loggerFactory.CreateLogger("VoicevoxRunCached");

        // 毎回の起動で出る定型情報なので Debug (Hooks などから繰り返し呼ぶとノイズになる)
        logger.LogDebug("VoicevoxRunCached アプリケーションを開始します - バージョン {Version}", AssemblyVersion.Current);

        return (settings, logger);
    }

    /// <summary>
    /// 設定ファイルとコマンドライン引数を読み込み、<see cref="AppSettings"/> にバインドする。
    /// 読み込みやバインドに失敗したら <see cref="ConfigurationException"/> (終了コード 7) にする。
    /// 値の範囲などの検証は別 (<see cref="ConfigurationValidationService"/>)。
    /// </summary>
    /// <param name="args">コマンドライン引数</param>
    /// <param name="basePath">appsettings.json を探すディレクトリ。省略時は実行ファイルのディレクトリ</param>
    public static AppSettings LoadSettings(string[] args, string? basePath = null)
    {
        try
        {
            var configuration = ConfigurationManager.BuildConfigurationWithCommandLine(args, basePath);
            return BuildAppSettings(configuration);
        }
        // InvalidDataException: JSON の構文エラー (FileConfigurationProvider がパーサの例外を包む)
        // FileNotFoundException: appsettings.json が無い
        // InvalidOperationException: 値をプロパティの型に変換できない (ConfigurationBinder。"Volume": "abc" など)
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or InvalidOperationException)
        {
            // 外側の例外は「読み込みに失敗した」としか言わず、どこが悪いかは内側 (行番号・キー名) にあるので全部並べる
            var details = String.Join(Environment.NewLine, ExceptionChain(ex).Select(e => $"- {e.Message}"));
            throw new ConfigurationException(
                ErrorCodes.Configuration.InvalidSettings,
                $"Failed to load configuration: {ex.Message}",
                $"設定の読み込みに失敗しました。{Environment.NewLine}{details}",
                ex,
                suggestedSolution: "appsettings.json (またはコマンドラインの --Section:Key=値) を確認してエラー箇所を修正してください。");
        }
    }

    private static IEnumerable<Exception> ExceptionChain(Exception ex)
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            yield return current;
        }
    }

    /// <summary>
    /// アプリケーション設定を構築します
    /// </summary>
    private static AppSettings BuildAppSettings(IConfiguration configuration)
    {
        var settings = new AppSettings();
        configuration.GetSection("VoiceVox").Bind(settings.VoiceVox);
        configuration.GetSection("Cache").Bind(settings.Cache);
        configuration.GetSection("Audio").Bind(settings.Audio);
        configuration.GetSection("Filler").Bind(settings.Filler);
        configuration.GetSection("Logging").Bind(settings.Logging);
        configuration.GetSection("Test").Bind(settings.Test);

        // 相対パス + UseExecutableBaseDirectory=true の場合、検証前に絶対パスへ解決しておく
        // (検証はパスが書き込み可能か実際に試すため、相対パスのままだと cwd 依存になる)
        settings.Cache.Directory = ResolvePath(settings.Cache.Directory, settings.Cache.UseExecutableBaseDirectory);
        settings.Filler.Directory = ResolvePath(settings.Filler.Directory, settings.Filler.UseExecutableBaseDirectory);

        return settings;
    }

    private static string ResolvePath(string path, bool useExecutableBaseDirectory)
    {
        if (String.IsNullOrWhiteSpace(path) || !useExecutableBaseDirectory || Path.IsPathRooted(path))
        {
            return path;
        }

        var executablePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
        var executableDirectory = Path.GetDirectoryName(executablePath) ?? Directory.GetCurrentDirectory();
        return Path.GetFullPath(Path.Combine(executableDirectory, path));
    }

    /// <summary>
    /// 設定を検証します
    /// </summary>
    private static void ValidateConfiguration(AppSettings settings)
    {
        var validationService = new ConfigurationValidationService();
        validationService.ValidateConfiguration(settings);
    }

    /// <summary>
    /// リダイレクトされている stdout / stderr を BOM なし UTF-8 で書くようにする。
    /// </summary>
    /// <remarks>
    /// 既定ではコンソールのコードページ (日本語 Windows は CP932) で書かれ、UTF-8 として読む側
    /// (Claude Code の Hooks・jq・Git Bash) で文字化けする。
    /// <see cref="Console.OutputEncoding"/> は使わない。内部で SetConsoleOutputCP を呼び、
    /// 終了後も呼び出し元のコンソールのコードページを変えてしまうため。コンソールに直接出す場合は従来どおり。
    /// </remarks>
    private static void ConfigureRedirectedOutputEncoding()
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        if (Console.IsOutputRedirected)
        {
            Console.SetOut(TextWriter.Synchronized(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true }));
        }

        if (Console.IsErrorRedirected)
        {
            Console.SetError(TextWriter.Synchronized(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true }));
        }
    }

    /// <summary>
    /// ANSIカラーを有効化します (エラー・ログは stderr に出すので stdout と stderr の両方)
    /// </summary>
    private static void EnableAnsiColors()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        foreach (var stdHandle in (int[])[StdOutputHandle, StdErrorHandle])
        {
            var handle = GetStdHandle(stdHandle);
            if (GetConsoleMode(handle, out uint mode))
            {
                SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
            }
        }
    }

    // P/Invoke declarations
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}
