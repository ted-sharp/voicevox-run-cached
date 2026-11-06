using Serilog;
using Serilog.Events;
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

    /// <summary>
    /// コンソール出力の設定
    /// </summary>
    /// <remarks>
    /// ログはすべて stderr に出す (stdout は <c>devices --json</c> 等のコマンドの結果だけにする)。
    /// </remarks>
    private void ConfigureConsoleLogging(LoggerConfiguration config)
    {
        if (_settings.Format.ToLowerInvariant() == "json")
        {
            // JSON形式の構造化ログ
            config.WriteTo.Console(
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}",
                standardErrorFromLevel: LogEventLevel.Verbose,
                theme: AnsiConsoleTheme.Code);
        }
        else
        {
            // シンプルな形式（デフォルト）
            config.WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}",
                standardErrorFromLevel: LogEventLevel.Verbose,
                theme: AnsiConsoleTheme.Code);
        }
    }

    /// <summary>
    /// ファイル出力の設定
    /// </summary>
    private void ConfigureFileLogging(LoggerConfiguration config)
    {
        var logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        Directory.CreateDirectory(logDirectory);

        var logFilePath = Path.Combine(logDirectory, "voicevox-run-cached-.log");

        config.WriteTo.File(
            path: logFilePath,
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: _settings.MaxFileCount,
            fileSizeLimitBytes: _settings.MaxFileSizeMb * 1024 * 1024,
            rollOnFileSizeLimit: true,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}",
            shared: true,
            flushToDiskInterval: TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// ログレベルの取得
    /// </summary>
    private LogEventLevel GetLogLevel(string level)
    {
        return level.ToLowerInvariant() switch
        {
            "trace" => LogEventLevel.Verbose,
            "debug" => LogEventLevel.Debug,
            "info" => LogEventLevel.Information,
            "warn" => LogEventLevel.Warning,
            "error" => LogEventLevel.Error,
            "crit" => LogEventLevel.Fatal,
            "none" => LogEventLevel.Fatal,
            _ => LogEventLevel.Information
        };
    }

}
