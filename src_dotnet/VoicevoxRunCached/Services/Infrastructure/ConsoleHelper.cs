namespace VoicevoxRunCached.Services.Infrastructure;

/// <summary>
/// ユーザー向けのコンソール出力。ログ書き込みは行わないため、ログに残したい情報は呼び出し側で ILogger を別途呼ぶこと。
/// </summary>
/// <remarks>
/// 出力先は役割で分ける。コマンドの結果・進捗は stdout、エラー・警告とその補足は stderr
/// (Claude Code の Hooks は終了コードが 0 / 2 以外のとき stderr をユーザーに表示する。stdout は <c>devices --json</c> 等の結果だけにする)。
/// 色はその出力先がリダイレクトされていないときだけ付ける (Hooks・パイプ・ファイルに ANSI エスケープを残さない)。
/// </remarks>
public static class ConsoleHelper
{
    private const string Reset = "\e[0m";
    private const string Red = "\e[31m";
    private const string Green = "\e[32m";
    private const string Yellow = "\e[33m";

    public static void WriteSuccess(string message) => WriteStdout(Green, message);
    public static void WriteLine(string message) => Console.Out.WriteLine(message);

    public static void WriteError(string message) => WriteStderr(Red, message);
    public static void WriteWarning(string message) => WriteStderr(Yellow, message);

    /// <summary>
    /// エラー・警告に続く補足 (解決策・コンテキスト・エラーコード) を色なしで stderr に書く。
    /// </summary>
    public static void WriteErrorDetail(string message) => Console.Error.WriteLine(message);

    private static void WriteStdout(string color, string message)
        => Console.Out.WriteLine(Console.IsOutputRedirected ? message : $"{color}{message}{Reset}");

    private static void WriteStderr(string color, string message)
        => Console.Error.WriteLine(Console.IsErrorRedirected ? message : $"{color}{message}{Reset}");
}
