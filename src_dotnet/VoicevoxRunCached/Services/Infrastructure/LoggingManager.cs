using Serilog;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Json;
using Serilog.Sinks.SystemConsole.Themes;
using VoicevoxRunCached.Configuration;

namespace VoicevoxRunCached.Services.Infrastructure;

/// <summary>
/// Serilog の初期化を担う。コンソール / ファイル出力の構成は LoggingSettings で制御する。
/// </summary>
public class LoggingManager
{
    private readonly LoggingSettings _settings;

    public LoggingManager(LoggingSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// Serilogの設定を構成（構造化ログ対応）
    /// </summary>
    public void ConfigureSerilog()
    {
        var loggerConfiguration = new LoggerConfiguration()
            .MinimumLevel.Is(GetLogLevel(_settings.Level))
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "VoicevoxRunCached")
            .Enrich.WithProperty("Version", AssemblyVersion.Current)
            .Enrich.WithProperty("Environment", Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production");

        // コンソール出力の設定
        ConfigureConsoleLogging(loggerConfiguration);

        // ファイル出力の設定（有効な場合）
        if (_settings.EnableFileLogging)
        {
            ConfigureFileLogging(loggerConfiguration);
        }

        // グローバルロガーを設定
        Log.Logger = loggerConfiguration.CreateLogger();

        // アプリケーション開始ログ (毎回出る定型情報なので Debug)
        Log.Debug("VoicevoxRunCached アプリケーションが開始されました - ログレベル: {LogLevel}, ファイル出力: {FileLogging}",
            _settings.Level, _settings.EnableFileLogging);
    }

    private const string SimpleConsoleTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}";
    private const string SimpleFileTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}";

    private bool IsJsonFormat => _settings.Format.Equals("json", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <c>Logging.Format = json</c> で使うフォーマッタ。1 イベント 1 行の JSON
    /// (Timestamp / Level / MessageTemplate / RenderedMessage / Properties / Exception)。
    /// </summary>
    /// <remarks>
    /// Serilog 本体の JsonFormatter を使う (Serilog.Formatting.Compact を追加するほどの要件は無い)。
    /// </remarks>
    public static ITextFormatter CreateJsonFormatter() => new JsonFormatter(renderMessage: true);

    /// <summary>
    /// コンソール出力の設定
    /// </summary>
    /// <remarks>
    /// ログはすべて stderr に出す (stdout は <c>devices --json</c> 等のコマンドの結果だけにする)。
    /// </remarks>
    private void ConfigureConsoleLogging(LoggerConfiguration config)
    {
        if (IsJsonFormat)
        {
            config.WriteTo.Console(CreateJsonFormatter(), standardErrorFromLevel: LogEventLevel.Verbose);
        }
        else
        {
            config.WriteTo.Console(
                outputTemplate: SimpleConsoleTemplate,
                standardErrorFromLevel: LogEventLevel.Verbose,
                theme: AnsiConsoleTheme.Code);
        }
    }

    /// <summary>
    /// ファイル出力の設定 (書式はコンソールと同じく Logging.Format に従う)
    /// </summary>
    private void ConfigureFileLogging(LoggerConfiguration config)
    {
        var logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        Directory.CreateDirectory(logDirectory);

        var logFilePath = Path.Combine(logDirectory, "voicevox-run-cached-.log");
        var fileSizeLimitBytes = _settings.MaxFileSizeMb * 1024L * 1024;

        if (IsJsonFormat)
        {
            config.WriteTo.File(
                CreateJsonFormatter(),
                path: logFilePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: _settings.MaxFileCount,
                fileSizeLimitBytes: fileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                shared: true,
                flushToDiskInterval: TimeSpan.FromSeconds(1));
        }
        else
        {
            config.WriteTo.File(
                path: logFilePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: _settings.MaxFileCount,
                fileSizeLimitBytes: fileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                outputTemplate: SimpleFileTemplate,
                shared: true,
                flushToDiskInterval: TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>
    /// <c>Logging.Level</c> の文字列を Serilog のレベルに変換する。<c>none</c> はログを一切出さない。
    /// </summary>
    public static LogEventLevel GetLogLevel(string level)
    {
        return level.ToLowerInvariant() switch
        {
            "trace" => LogEventLevel.Verbose,
            "debug" => LogEventLevel.Debug,
            "info" => LogEventLevel.Information,
            "warn" => LogEventLevel.Warning,
            "error" => LogEventLevel.Error,
            "crit" => LogEventLevel.Fatal,
            "none" => LevelAlias.Off,
            _ => LogEventLevel.Information
        };
    }

}
