using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;
using VoicevoxRunCached.Configuration;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace VoicevoxRunCached.Services;

public class LoggingManager
{
    public static (LogLevel Level, bool UseJson) ParseLogOptions(string[] args, AppSettings settings)
    {
        // verbose implies Debug unless overridden explicitly
        var verbose = args.Contains("--verbose");
        var levelStr = (GetStringOption(args, "--log-level") ?? settings.Logging.Level.ToString()).ToLowerInvariant();
        var fmtStr = (GetStringOption(args, "--log-format") ?? settings.Logging.Format).ToLowerInvariant();

        LogLevel level = verbose ? LogLevel.Debug : LogLevel.Information;
        level = levelStr switch
        {
            "trace" => LogLevel.Trace,
            "debug" => LogLevel.Debug,
            "info" or "information" => LogLevel.Information,
            "warn" or "warning" => LogLevel.Warning,
            "error" => LogLevel.Error,
            "crit" or "critical" => LogLevel.Critical,
            "none" => LogLevel.None,
            _ => level
        };

        bool useJson = fmtStr == "json";
        return (level, useJson);
    }

    public static void ConfigureSerilog(Microsoft.Extensions.Configuration.IConfiguration configuration, string[] args, AppSettings settings, LogLevel minLogLevel, bool useJsonConsole)
    {
        // Read sinks/enrichers from configuration first
        var loggerConfig = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithThreadId()
            // Override minimum level from CLI/settings
            .MinimumLevel.Is(MapToSerilogLevel(minLogLevel));

        // Note: If JSON console is requested, prefer Serilog configuration to define JSON console sink.
        // To avoid duplicate console outputs, we do not add another console sink here.

        Log.Logger = loggerConfig.CreateLogger();
    }

    public static ILoggerFactory CreateLoggerFactory(LogLevel minLogLevel, bool useJsonConsole)
    {
        // Use Serilog as the sole provider to avoid double console/file outputs
        return LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(minLogLevel);
            builder.AddSerilog(Log.Logger, dispose: false);
        });
    }

    private static string? GetStringOption(string[] args, string option)
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

    private static LogEventLevel MapToSerilogLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => LogEventLevel.Verbose,
        LogLevel.Debug => LogEventLevel.Debug,
        LogLevel.Information => LogEventLevel.Information,
        LogLevel.Warning => LogEventLevel.Warning,
        LogLevel.Error => LogEventLevel.Error,
        LogLevel.Critical => LogEventLevel.Fatal,
        LogLevel.None => LogEventLevel.Fatal, // effectively mute most logs
        _ => LogEventLevel.Information
    };
}
