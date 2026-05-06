using Microsoft.Extensions.Logging;

namespace VoicevoxRunCached.Services.Infrastructure;

/// <summary>
/// 未知例外の表示を担うユーティリティ。
/// 想像上の親切メッセージは作らない: 元例外の Message をそのままユーザーに見せる。
/// </summary>
public static class ErrorHandlingUtility
{
    public static int HandleExceptionAndGetExitCode(Exception ex, ILogger? logger = null, string? operation = null, string? context = null)
    {
        if (logger != null && !String.IsNullOrEmpty(operation))
        {
            logger.LogError(ex, "予期しないエラー - 操作: {Operation}, コンテキスト: {Context}",
                operation, context ?? "なし");
        }

        ConsoleHelper.WriteError($"Error: {ex.Message}");
        return 1;
    }
}
