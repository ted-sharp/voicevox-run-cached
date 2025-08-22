using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Serilog;
using System.Runtime.InteropServices;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Services;
using VoicevoxRunCached.Configuration.Validators;

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

        // Initialize configuration (appsettings + CLI) and validate once
        var configuration = VoicevoxRunCached.Services.ConfigurationManager.BuildConfigurationWithCommandLine(args);
        var settings = new AppSettings();
        configuration.GetSection("VoiceVox").Bind(settings.VoiceVox);
        configuration.GetSection("Cache").Bind(settings.Cache);
        configuration.GetSection("Audio").Bind(settings.Audio);
        configuration.GetSection("Filler").Bind(settings.Filler);
        configuration.GetSection("Logging").Bind(settings.Logging);
        configuration.GetSection("Test").Bind(settings.Test);

        var validationService = new ConfigurationValidationService();
        try
        {
            validationService.ValidateConfiguration(settings);
            ConsoleHelper.WriteValidationSuccess("設定の検証が完了しました", null);
        }
        catch (InvalidOperationException ex)
        {
            ConsoleHelper.WriteValidationError($"設定の検証に失敗しました: {ex.Message}", null);
            return 1;
        }

        // Configure logging
        var loggingManager = new LoggingManager(settings.Logging, configuration);
        loggingManager.ConfigureSerilog();

        // Create logger factory
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(Log.Logger, dispose: false);
        });
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

            logger.LogInformation("Test command executed with message: {TestMessage}", testMessage);
            ConsoleHelper.WriteLine($"テストメッセージ: {testMessage}", logger);

            // Replace first arg with the configured message and keep other options
            var remaining = args.Skip(1).Where(arg => arg != null).Cast<string>().ToArray();
            var testArgs = new[] { testMessage }.Concat(remaining).ToArray();

            // デバッグ用のログ出力
            logger.LogInformation("Test args constructed: {TestArgs}", string.Join(" ", testArgs));
            ConsoleHelper.WriteLine($"Debug: Test args: {string.Join(" ", testArgs)}", logger);

            // Parse arguments and handle text-to-speech with test args
            var testRequest = ArgumentParser.ParseArguments(testArgs, settings);
            if (testRequest == null)
            {
                Console.WriteLine($"\e[31mError: Invalid arguments\e[0m");
                ArgumentParser.ShowUsage();
                return 1;
            }

            string? testOutPath = ArgumentParser.GetStringOption(testArgs, "--out") ?? ArgumentParser.GetStringOption(testArgs, "-o");
            bool testNoPlay = ArgumentParser.GetBoolOption(testArgs, "--no-play");

            // Setup cancellation (Ctrl+C)
            using var testCts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                logger.LogWarning("Cancellation requested (Ctrl+C). Attempting graceful shutdown...");
                Log.Warning("ユーザーによるキャンセル要求 (Ctrl+C)。正常終了を試行します...");
                testCts.Cancel();
            };

            var testResult = await commandHandler.HandleTextToSpeechAsync(
                testRequest,
                ArgumentParser.GetBoolOption(testArgs, "--no-cache"),
                ArgumentParser.GetBoolOption(testArgs, "--cache-only"),
                ArgumentParser.GetBoolOption(testArgs, "--verbose"),
                testOutPath,
                testNoPlay,
                testCts.Token);

            // Cleanup Serilog before exit
            ProgramExtensions.CleanupSerilog();

            return testResult;
        }

        // Parse arguments and handle text-to-speech (only for non-test commands)
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
