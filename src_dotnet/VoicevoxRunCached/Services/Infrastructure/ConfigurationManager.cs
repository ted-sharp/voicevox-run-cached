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

    /// <param name="args">コマンドライン引数</param>
    /// <param name="basePath">appsettings.json を探すディレクトリ。省略時は実行ファイルのディレクトリ</param>
    public static IConfiguration BuildConfigurationWithCommandLine(string[] args, string? basePath = null)
    {
        var processedArgs = ArgumentParser.PreprocessArgs(args);

        if (basePath is null)
        {
            var executablePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
            basePath = Path.GetDirectoryName(executablePath) ?? Directory.GetCurrentDirectory();
        }

        return new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false)
            .AddCommandLine(processedArgs, SwitchMappings.ToDictionary())
            .Build();
    }
}
