using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Models;
using VoicevoxRunCached.Services;
using Serilog;

namespace VoicevoxRunCached;

class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    private const int STD_OUTPUT_HANDLE = -11;
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

    static async Task<int> Main(string[] args)
    {
        EnableAnsiColors();

        // Initialize Media Foundation once per process
        MediaFoundationManager.Initialize();

        // Initialize configuration and validation with command-line arguments
        var configManager = new ConfigurationManager();
        var configuration = ConfigurationManager.BuildConfigurationWithCommandLine(args);
        var settings = new AppSettings();
        // 基本的な設定のみを読み込み
        if (Int32.TryParse(configuration["VoiceVox:DefaultSpeaker"], out int defaultSpeaker))
            settings.VoiceVox.DefaultSpeaker = defaultSpeaker;
        if (Boolean.TryParse(configuration["Filler:Enabled"], out bool fillerEnabled))
            settings.Filler.Enabled = fillerEnabled;

        if (!configManager.ValidateConfiguration(settings, null!))
        {
            return 1;
        }

        // Configure logging
        var (minLogLevel, useJsonConsole) = LoggingManager.ParseLogOptions(args, settings);
        LoggingManager.ConfigureSerilog(configuration, args, settings, minLogLevel, useJsonConsole);

        using var loggerFactory = LoggingManager.CreateLoggerFactory(minLogLevel, useJsonConsole);
        var logger = loggerFactory.CreateLogger("VoicevoxRunCached");

        // Log application startup
        logger.LogInformation("VoicevoxRunCached アプリケーションを開始します - バージョン {Version}", GetVersion());

        // Create command handler with updated settings
        var commandHandler = new CommandHandler(settings, logger);

        // Handle commands
        if (args.Length == 0 || args[0] == "--help" || args[0] == "-h")
        {
            ArgumentParser.ShowUsage();
            return 0;
        }

        if (args[0] == "speakers")
        {
            return await commandHandler.HandleSpeakersAsync();
        }

        if (args[0] == "devices")
        {
            var subArgs = args.Skip(1).ToArray();
            return commandHandler.HandleDevices(subArgs);
        }

        if (args[0] == "--init")
        {
            return await commandHandler.HandleInitAsync();
        }

        if (args[0] == "--clear")
        {
            return await commandHandler.HandleClearCacheAsync();
        }

        if (args[0] == "--benchmark")
        {
            return await commandHandler.HandleBenchmarkAsync();
        }

        // Handle --test command
        if (args[0] == "--test")
        {
            var testMessage = settings.Test?.Message ?? String.Empty;
            if (String.IsNullOrWhiteSpace(testMessage))
            {
                Console.WriteLine("\e[31mError: Test.Message is empty in configuration\e[0m");
                return 1;
            }

            // Replace first arg with the configured message and keep other options
            var remaining = args.Skip(1).Where(arg => arg != null).Cast<string>().ToArray();
            args = new[] { testMessage }.Concat(remaining).ToArray();
        }

        // Parse arguments and handle text-to-speech
        var request = ArgumentParser.ParseArguments(args, settings);
        if (request == null)
        {
            Console.WriteLine($"\e[31mError: Invalid arguments\e[0m");
            ArgumentParser.ShowUsage();
            return 1;
        }

        string? outPath = ArgumentParser.GetStringOption(args, "--out") ?? ArgumentParser.GetStringOption(args, "-o");
        bool noPlay = ArgumentParser.GetBoolOption(args, "--no-play");

        // Setup cancellation (Ctrl+C)
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            logger.LogWarning("Cancellation requested (Ctrl+C). Attempting graceful shutdown...");
            Log.Warning("ユーザーによるキャンセル要求 (Ctrl+C)。正常終了を試行します...");
            cts.Cancel();
        };

        var result = await commandHandler.HandleTextToSpeechAsync(
            request,
            ArgumentParser.GetBoolOption(args, "--no-cache"),
            ArgumentParser.GetBoolOption(args, "--cache-only"),
            ArgumentParser.GetBoolOption(args, "--verbose"),
            outPath,
            noPlay,
            cts.Token);

        // Cleanup Serilog before exit
        ProgramExtensions.CleanupSerilog();

        return result;
    }

    private static void EnableAnsiColors()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var handle = GetStdHandle(STD_OUTPUT_HANDLE);
            if (GetConsoleMode(handle, out uint mode))
            {
                mode |= ENABLE_VIRTUAL_TERMINAL_PROCESSING;
                SetConsoleMode(handle, mode);
            }
        }
    }

















    private static string GetVersion()
    {
        return typeof(Program).Assembly.GetName().Version?.ToString() ?? "Unknown";
    }
}

// Serilogの適切なクローズ処理のための拡張メソッド
public static class ProgramExtensions
{
    public static void CleanupSerilog()
    {
        try
        {
            Log.CloseAndFlush();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Failed to cleanup Serilog: {ex.Message}");
        }
    }
}
