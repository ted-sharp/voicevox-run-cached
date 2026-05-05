using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Exceptions;

namespace VoicevoxRunCached.Services.Infrastructure;

/// <summary>
/// 終了コード解決と未知例外の表示を担うユーティリティ。
/// 想像上の親切メッセージは作らない: 元例外の Message をそのままユーザーに見せる。
/// </summary>
public static class ErrorHandlingUtility
{
    public static int GetExitCodeFromErrorCode(string errorCode)
    {
        if (String.IsNullOrEmpty(errorCode))
            return 1;

        var category = ErrorCodes.GetCategory(errorCode);
        return category switch
        {
            "Configuration" => 2,
            "Engine" => 3,
            "Cache" => 4,
            "Audio" => 5,
            "API" => 6,
            _ => 1,
        };
    }

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
