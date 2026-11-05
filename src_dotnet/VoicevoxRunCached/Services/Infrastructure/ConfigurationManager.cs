using Microsoft.Extensions.Configuration;

namespace VoicevoxRunCached.Services.Infrastructure;

public static class ConfigurationManager
{
    /// <summary>
    /// CLI オプション → 設定キーの対応。対応付けしないと <c>--log-level debug</c> は
    /// "log-level" というキーになり <see cref="Configuration.LoggingSettings"/> に入らない。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> SwitchMappings = new Dictionary<string, string>
    {
        ["--log-level"] = "Logging:Level",
        ["--log-format"] = "Logging:Format",
    };

    public static IConfiguration BuildConfigurationWithCommandLine(string[] args)
    {
        var processedArgs = ArgumentParser.PreprocessArgs(args);

        var executablePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
        var executableDirectory = Path.GetDirectoryName(executablePath) ?? Directory.GetCurrentDirectory();

        return new ConfigurationBuilder()
            .SetBasePath(executableDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true)
            .AddCommandLine(processedArgs, SwitchMappings.ToDictionary())
            .Build();
    }
}
