using Microsoft.Extensions.Configuration;

namespace VoicevoxRunCached.Services.Infrastructure;

public static class ConfigurationManager
{
    public static IConfiguration BuildConfigurationWithCommandLine(string[] args)
    {
        var processedArgs = ArgumentParser.PreprocessArgs(args);

        var executablePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
        var executableDirectory = Path.GetDirectoryName(executablePath) ?? Directory.GetCurrentDirectory();

        return new ConfigurationBuilder()
            .SetBasePath(executableDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true)
            .AddCommandLine(processedArgs)
            .Build();
    }
}
