using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Exceptions;

namespace VoicevoxRunCached.Services;

public static class ConsoleHelper
{
    public static void WriteSuccess(string message, ILogger? logger = null)
    {
        Console.WriteLine($"\e[32m{message}\e[0m");
        logger?.LogInformation(message);
    }

    public static void WriteError(string message, ILogger? logger = null)
    {
        Console.WriteLine($"\e[31m{message}\e[0m");
        logger?.LogError(message);
    }

    public static void WriteError(string message, Exception ex, ILogger? logger = null)
    {
        var errorMessage = ex is VoicevoxRunCachedException vrcEx
            ? $"{message} [{vrcEx.ErrorCode}] - {ErrorCodes.GetDescription(vrcEx.ErrorCode)}"
            : $"{message} - {ex.Message}";

        Console.WriteLine($"\e[31m{errorMessage}\e[0m");
        logger?.LogError(ex, message);
    }

    public static void WriteWarning(string message, ILogger? logger = null)
    {
        Console.WriteLine($"\e[33m{message}\e[0m");
        logger?.LogWarning(message);
    }

    public static void WriteInfo(string message, ILogger? logger = null)
    {
        Console.WriteLine($"\e[36m{message}\e[0m");
        logger?.LogInformation(message);
    }

    public static void WriteDebug(string message, ILogger? logger = null)
    {
        Console.WriteLine($"\e[90m{message}\e[0m");
        logger?.LogDebug(message);
    }

    public static void WriteLine(string message, ILogger? logger = null)
    {
        Console.WriteLine(message);
        logger?.LogInformation(message);
    }

    public static void WriteValidationSuccess(string message, ILogger? logger = null)
    {
        Console.WriteLine($"\e[32m✓ {message}\e[0m");
        logger?.LogInformation(message);
    }

    public static void WriteValidationError(string message, ILogger? logger = null)
    {
        Console.WriteLine($"\e[31m✗ {message}\e[0m");
        logger?.LogError(message);
    }
}
