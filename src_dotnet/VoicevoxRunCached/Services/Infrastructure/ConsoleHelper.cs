namespace VoicevoxRunCached.Services.Infrastructure;

/// <summary>
/// 色付きコンソール出力。ログ書き込みは行わないため、ログに残したい情報は呼び出し側で ILogger を別途呼ぶこと。
/// </summary>
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

    public static void WriteSuccess(string message) => WriteColored(Green, message);
    public static void WriteError(string message) => WriteColored(Red, message);
    public static void WriteWarning(string message) => WriteColored(Yellow, message);
    public static void WriteInfo(string message) => WriteColored(Cyan, message);
    public static void WriteDebug(string message) => WriteColored(Gray, message);
    public static void WriteLine(string message) => Console.WriteLine(message);
}
