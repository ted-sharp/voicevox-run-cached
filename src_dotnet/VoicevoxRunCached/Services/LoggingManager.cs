using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;
using VoicevoxRunCached.Configuration;

namespace VoicevoxRunCached.Services;

/// <summary>
/// 構造化ログとパフォーマンスメトリクスを活用したログ管理サービス
/// </summary>
public class LoggingManager
{
    private readonly LoggingSettings _settings;
    private bool _isInitialized;

    public LoggingManager(LoggingSettings settings, IConfiguration configuration)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        ArgumentNullException.ThrowIfNull(configuration);
    }

    /// <summary>
    /// Serilogの設定を構成（構造化ログ対応）
    /// </summary>
    public void ConfigureSerilog()
    {
        if (_isInitialized)
        {
            Log.Warning("Serilogは既に初期化されています");
            return;
        }

        var loggerConfiguration = new LoggerConfiguration()
            .MinimumLevel.Is(GetLogLevel(_settings.Level))
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "VoicevoxRunCached")
            .Enrich.WithProperty("Version", GetApplicationVersion())
            .Enrich.WithProperty("Environment", Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production");

        // コンソール出力の設定
        ConfigureConsoleLogging(loggerConfiguration);

        // ファイル出力の設定（有効な場合）
        if (_settings.EnableFileLogging)
        {
            ConfigureFileLogging(loggerConfiguration);
        }

        // パフォーマンスメトリクスの設定
        ConfigurePerformanceLogging(loggerConfiguration);

        // グローバルロガーを設定
        Log.Logger = loggerConfiguration.CreateLogger();

        // アプリケーション開始ログ
        Log.Information("VoicevoxRunCached アプリケーションが開始されました - ログレベル: {LogLevel}, ファイル出力: {FileLogging}",
            _settings.Level, _settings.EnableFileLogging);

        _isInitialized = true;
    }

    /// <summary>
    /// コンソール出力の設定
    /// </summary>
    private void ConfigureConsoleLogging(LoggerConfiguration config)
    {
        if (_settings.Format.ToLowerInvariant() == "json")
        {
            // JSON形式の構造化ログ
            config.WriteTo.Console(
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}",
                theme: AnsiConsoleTheme.Code);
        }
        else
        {
            // シンプルな形式（デフォルト）
            config.WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}",
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
    /// パフォーマンスメトリクスの設定
    /// </summary>
    private void ConfigurePerformanceLogging(LoggerConfiguration config)
    {
        // パフォーマンス関連のログを特別に処理
        config.WriteTo.Logger(lc => lc
            .Filter.ByIncludingOnly(e => e.Properties.ContainsKey("PerformanceMetric"))
            .WriteTo.Console(outputTemplate: "[PERF] {Message:lj} {Properties:j}{NewLine}"));
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

    /// <summary>
    /// アプリケーションバージョンの取得
    /// </summary>
    private string GetApplicationVersion()
    {
        try
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version;
            return version?.ToString() ?? "1.0.0.0";
        }
        catch
        {
            return "1.0.0.0";
        }
    }

}
