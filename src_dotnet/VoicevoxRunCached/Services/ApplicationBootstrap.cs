using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using VoicevoxRunCached.Configuration;

namespace VoicevoxRunCached.Services;

/// <summary>
/// アプリケーションの初期化と設定を行うクラス
/// </summary>
public class ApplicationBootstrap
{
    private const int STD_OUTPUT_HANDLE = -11;
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

    /// <summary>
    /// アプリケーションの初期化を実行します
    /// </summary>
    /// <param name="args">コマンドライン引数</param>
    /// <returns>初期化された設定とロガー</returns>
    public static async Task<(AppSettings settings, Microsoft.Extensions.Logging.ILogger logger)> InitializeAsync(string[] args)
    {
        // ANSIカラーを有効化
        EnableAnsiColors();

        // 設定を構築
        var configuration = ConfigurationManager.BuildConfigurationWithCommandLine(args);
        var settings = BuildAppSettings(configuration);

        // 設定を検証
        await ValidateConfigurationAsync(settings);

        // ログを設定
        var loggingManager = new LoggingManager(settings.Logging, configuration);
        loggingManager.ConfigureSerilog();

        // ロガーファクトリを作成
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(Log.Logger, dispose: false);
        });
        var logger = loggerFactory.CreateLogger("VoicevoxRunCached");

        // Media Foundation初期化（ロガー作成後）
        var mediaFoundationInitializer = MediaFoundationInitializer.GetInstance(logger);
        mediaFoundationInitializer.Initialize();

        // アプリケーション開始をログ出力
        logger.LogInformation("VoicevoxRunCached アプリケーションを開始します - バージョン {Version}", GetVersion());

        return (settings, logger);
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
        return settings;
    }

    /// <summary>
    /// 設定を検証します
    /// </summary>
    private static Task ValidateConfigurationAsync(AppSettings settings)
    {
        var validationService = new ConfigurationValidationService();
        try
        {
            validationService.ValidateConfiguration(settings);
            ConsoleHelper.WriteValidationSuccess("設定の検証が完了しました", null);
        }
        catch (InvalidOperationException ex)
        {
            ConsoleHelper.WriteValidationError($"設定の検証に失敗しました: {ex.Message}", null);
            throw;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// ANSIカラーを有効化します
    /// </summary>
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

    /// <summary>
    /// バージョンを取得します
    /// </summary>
    private static string GetVersion()
    {
        return typeof(ApplicationBootstrap).Assembly.GetName().Version?.ToString() ?? "Unknown";
    }

    // P/Invoke declarations
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}
