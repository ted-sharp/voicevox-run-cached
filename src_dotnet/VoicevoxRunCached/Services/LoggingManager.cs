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
        var logLevel = useJsonConsole ? LogEventLevel.Debug : LogEventLevel.Information;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(logLevel)
            .Enrich.FromLogContext()
            .Enrich.WithMachineName()
            .Enrich.WithThreadId()
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                path: "./logs/voicevox-.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    public static ILoggerFactory CreateLoggerFactory(LogLevel minLogLevel, bool useJsonConsole)
    {
        return LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(minLogLevel);
            if (useJsonConsole)
            {
                builder.AddJsonConsole(o =>
                {
                    o.TimestampFormat = "HH:mm:ss.fff ";
                    o.IncludeScopes = false;
                });
            }
            else
            {
                builder.AddSimpleConsole(o =>
                {
                    o.TimestampFormat = "HH:mm:ss.fff ";
                    o.SingleLine = true;
                    o.IncludeScopes = false;
                });
            }
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
}
