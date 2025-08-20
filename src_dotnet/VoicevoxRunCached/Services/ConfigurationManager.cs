using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Binder;
using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;
using VoicevoxRunCached.Services;

namespace VoicevoxRunCached.Services;

public class ConfigurationManager
{
    private readonly IConfiguration _configuration;
    private readonly ConfigurationValidationService _validationService;

    public ConfigurationManager()
    {
        this._configuration = BuildConfiguration();
        this._validationService = new ConfigurationValidationService();
    }

    public AppSettings GetSettings()
    {
        var settings = new AppSettings();

        // 設定ファイルから直接バインド
        this._configuration.GetSection("VoiceVox").Bind(settings.VoiceVox);
        this._configuration.GetSection("Cache").Bind(settings.Cache);
        this._configuration.GetSection("Audio").Bind(settings.Audio);
        this._configuration.GetSection("Filler").Bind(settings.Filler);
        this._configuration.GetSection("Logging").Bind(settings.Logging);
        this._configuration.GetSection("Test").Bind(settings.Test);

        return settings;
    }

    public bool ValidateConfiguration(AppSettings settings, ILogger? logger = null)
    {
        try
        {
            this._validationService.ValidateConfiguration(settings);
            ConsoleHelper.WriteValidationSuccess("設定の検証が完了しました", logger);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            ConsoleHelper.WriteValidationError($"設定の検証に失敗しました: {ex.Message}", logger);
            return false;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "設定検証中に予期しないエラーが発生しました");
            ConsoleHelper.WriteValidationError($"設定検証中に予期しないエラーが発生しました: {ex.Message}", logger);
            return false;
        }
    }

    public IConfiguration GetConfiguration()
    {
        return this._configuration;
    }

    private static IConfiguration BuildConfiguration()
    {
        // Use the directory where the executable is located, not the current working directory
        var executablePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
        var executableDirectory = Path.GetDirectoryName(executablePath) ?? Directory.GetCurrentDirectory();

        return new ConfigurationBuilder()
            .SetBasePath(executableDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();
    }

    /// <summary>
    /// コマンドライン引数を含む設定を構築
    /// </summary>
    public static IConfiguration BuildConfigurationWithCommandLine(string[] args)
    {
        // コマンドライン引数を前処理
        var processedArgs = ArgumentParser.PreprocessArgs(args);

        // Use the directory where the executable is located, not the current working directory
        var executablePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
        var executableDirectory = Path.GetDirectoryName(executablePath) ?? Directory.GetCurrentDirectory();

        return new ConfigurationBuilder()
            .SetBasePath(executableDirectory)
            // 設定ファイル（最低優先度）
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true)
            // コマンドライン引数（最高優先度）
            .AddCommandLine(processedArgs, ArgumentParser.Aliases)
            .Build();
    }
}
