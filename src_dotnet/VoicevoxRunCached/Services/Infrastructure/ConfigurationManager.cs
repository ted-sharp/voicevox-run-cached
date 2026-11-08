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
        // "-- <本文>" は区切りと本文を設定の読み込みから外す (コマンド名やオプションと同じ文字列の本文を読むための書き方なので、
        // 本文が "--init" や "--Logging:Level=error" でも設定として解釈しない)
        if (ArgumentParser.StartsWithTextSeparator(args))
        {
            args = args[Math.Min(2, args.Length)..];
        }
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
