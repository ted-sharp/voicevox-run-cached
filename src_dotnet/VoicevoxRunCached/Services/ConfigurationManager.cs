using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Configuration;

namespace VoicevoxRunCached.Services;

public class ConfigurationManager
{
    private readonly IConfiguration _configuration;
    private readonly ConfigurationValidationService _validationService;

    public ConfigurationManager()
    {
        _configuration = BuildConfiguration();
        _validationService = new ConfigurationValidationService();
    }

    public AppSettings GetSettings()
    {
        return _configuration.Get<AppSettings>() ?? new AppSettings();
    }

    public bool ValidateConfiguration(AppSettings settings, ILogger? logger = null)
    {
        try
        {
            _validationService.ValidateConfiguration(settings);
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
        return _configuration;
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
}
