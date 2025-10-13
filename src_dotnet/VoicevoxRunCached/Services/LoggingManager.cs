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

    /// <summary>
    /// パフォーマンスメトリクスの記録
    /// </summary>
    public static void LogPerformanceMetric(string operation, TimeSpan duration, Dictionary<string, object>? additionalData = null)
    {
        var properties = new Dictionary<string, object>
        {
            ["PerformanceMetric"] = true,
            ["Operation"] = operation,
            ["DurationMs"] = duration.TotalMilliseconds,
            ["Duration"] = duration.ToString(@"mm\:ss\.fff")
        };

        if (additionalData != null)
        {
            foreach (var kvp in additionalData)
            {
                properties[kvp.Key] = kvp.Value;
            }
        }

        Log.Information("パフォーマンスメトリクス - {Operation} 完了: {Duration} ({DurationMs:F2}ms)",
            operation, duration.ToString(@"mm\:ss\.fff"), duration.TotalMilliseconds);
    }

    /// <summary>
    /// メモリ使用量の記録
    /// </summary>
    public static void LogMemoryUsage(string context = "Current")
    {
        _ = GC.GetGCMemoryInfo();
        var totalMemory = GC.GetTotalMemory(false);
        var workingSet = Environment.WorkingSet;

        Log.Information("メモリ使用量 ({Context}) - ヒープ: {HeapSize:F2}MB, ワーキングセット: {WorkingSet:F2}MB",
            context,
            totalMemory / 1024.0 / 1024.0,
            workingSet / 1024.0 / 1024.0);
    }

    /// <summary>
    /// キャッシュ統計の記録
    /// </summary>
    public static void LogCacheStatistics(CacheStatistics stats)
    {
        Log.Information("キャッシュ統計 - 総アイテム数: {TotalItems}, 有効アイテム: {ValidItems}, 使用率: {UsagePercentage:F1}%, ヒット率: {HitRate:P1}",
            stats.TotalItems,
            stats.ValidItems,
            stats.UsagePercentage,
            stats.HitRate);
    }

    /// <summary>
    /// セグメント処理統計の記録
    /// </summary>
    public static void LogSegmentStatistics(SegmentStatistics stats)
    {
        Log.Information("セグメント処理統計 - 総セグメント数: {TotalSegments}, 平均長: {AverageLength:F1}, 最小長: {MinLength}, 最大長: {MaxLength}",
            stats.TotalSegments,
            stats.AverageLength,
            stats.MinLength,
            stats.MaxLength);
    }

    /// <summary>
    /// エラー統計の記録
    /// </summary>
    public static void LogErrorStatistics(string errorType, string context, Exception? exception = null)
    {
        var properties = new Dictionary<string, object>
        {
            ["ErrorType"] = errorType,
            ["Context"] = context,
            ["Timestamp"] = DateTime.UtcNow
        };

        if (exception != null)
        {
            properties["ExceptionType"] = exception.GetType().Name;
            properties["ExceptionMessage"] = exception.Message;
        }

        Log.Warning("エラー統計 - タイプ: {ErrorType}, コンテキスト: {Context}", errorType, context);
    }

    /// <summary>
    /// アプリケーション終了時のクリーンアップ
    /// </summary>
    public void Cleanup()
    {
        if (_isInitialized)
        {
            Log.Information("VoicevoxRunCached アプリケーションが終了します");
            Log.CloseAndFlush();
            _isInitialized = false;
        }
    }
}
