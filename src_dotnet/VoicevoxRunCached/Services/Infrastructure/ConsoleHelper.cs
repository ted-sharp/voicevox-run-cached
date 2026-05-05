using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Exceptions;

namespace VoicevoxRunCached.Services.Infrastructure;

public static class ConsoleHelper
{
    private const string Reset = "\e[0m";
    private const string Red = "\e[31m";
    private const string Green = "\e[32m";
    private const string Yellow = "\e[33m";
    private const string Cyan = "\e[36m";
    private const string Gray = "\e[90m";

    private static void WriteColored(string color, string message)
    {
        Console.WriteLine($"{color}{message}{Reset}");
    }

    public static void WriteSuccess(string message, ILogger? logger = null)
    {
        WriteColored(Green, message);
        logger?.LogInformation(message);
    }

    public static void WriteError(string message, ILogger? logger = null)
    {
        WriteColored(Red, message);
        logger?.LogError(message);
    }

    public static void WriteError(string message, Exception ex, ILogger? logger = null)
    {
        var errorMessage = ex is VoicevoxRunCachedException vrcEx
            ? $"{message} [{vrcEx.ErrorCode}] - {ErrorCodes.GetDescription(vrcEx.ErrorCode)}"
            : $"{message} - {ex.Message}";

        WriteColored(Red, errorMessage);
        logger?.LogError(ex, message);
    }

    public static void WriteWarning(string message, ILogger? logger = null)
    {
        WriteColored(Yellow, message);
        logger?.LogWarning(message);
    }

    public static void WriteInfo(string message, ILogger? logger = null)
    {
        WriteColored(Cyan, message);
        logger?.LogInformation(message);
    }

    public static void WriteDebug(string message, ILogger? logger = null)
    {
        WriteColored(Gray, message);
        logger?.LogDebug(message);
    }

    public static void WriteLine(string message, ILogger? logger = null)
    {
        Console.WriteLine(message);
        logger?.LogInformation(message);
    }

    public static void WriteValidationSuccess(string message, ILogger? logger = null)
    {
        WriteColored(Green, $"✓ {message}");
        logger?.LogInformation(message);
    }

    public static void WriteValidationError(string message, ILogger? logger = null)
    {
        WriteColored(Red, $"✗ {message}");
        logger?.LogError(message);
    }
}
