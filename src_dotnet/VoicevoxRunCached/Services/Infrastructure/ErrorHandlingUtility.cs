using System.Net;
using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Exceptions;

namespace VoicevoxRunCached.Services.Infrastructure;

/// <summary>
/// エラーハンドリングを統一化するためのユーティリティクラス
/// </summary>
public static class ErrorHandlingUtility
{
    /// <summary>
    /// エラーコード・ユーザーメッセージ・推奨解決策を 1 度の switch でまとめて取り出すための record。
    /// HTTP/例外ごとの分岐をここに集約することで、3 種の switch を 1 つに減らしている。
    /// </summary>
    public readonly record struct ErrorContext(string Code, string UserMessage, string Solution);

    /// <summary>
    /// HTTPステータスコードからエラー文脈 (Code, UserMessage, Solution) を取得します
    /// </summary>
    public static ErrorContext FromHttpStatus(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.BadRequest => new(
            ErrorCodes.Api.ApiRequestFailed,
            "リクエストが無効です。パラメータを確認してください。",
            "リクエストパラメータの形式と値を確認してください。"),
        HttpStatusCode.Unauthorized => new(
            ErrorCodes.Api.ApiAuthenticationError,
            "認証に失敗しました。APIキーまたは権限を確認してください。",
            "APIキーが正しく設定されているか確認してください。"),
        HttpStatusCode.Forbidden => new(
            ErrorCodes.General.PermissionDenied,
            "アクセスが拒否されました。権限を確認してください。",
            "必要な権限があるか確認してください。"),
        HttpStatusCode.NotFound => new(
            ErrorCodes.General.ResourceNotFound,
            "リソースが見つかりません。APIバージョンとエンドポイントを確認してください。",
            "APIエンドポイントのURLが正しいか確認してください。"),
        HttpStatusCode.TooManyRequests => new(
            ErrorCodes.Api.ApiRateLimitExceeded,
            "レート制限を超過しました。しばらく待ってから再試行してください。",
            "リクエスト頻度を下げてください。"),
        HttpStatusCode.InternalServerError => new(
            ErrorCodes.Engine.EngineProcessError,
            "サーバーで内部エラーが発生しました。しばらく待ってから再試行してください。",
            "VOICEVOXエンジンを再起動してください。"),
        HttpStatusCode.ServiceUnavailable => new(
            ErrorCodes.Engine.EngineNotAvailable,
            "サービスが一時的に利用できません。しばらく待ってから再試行してください。",
            "VOICEVOXエンジンが起動しているか確認してください。"),
        HttpStatusCode.GatewayTimeout => new(
            ErrorCodes.Api.ApiTimeout,
            "リクエストがタイムアウトしました。しばらく待ってから再試行してください。",
            "ネットワーク接続とVOICEVOXエンジンの応答時間を確認してください。"),
        HttpStatusCode.RequestTimeout => new(
            ErrorCodes.Api.ApiTimeout,
            "リクエストがタイムアウトしました。しばらく待ってから再試行してください。",
            "ネットワーク接続とVOICEVOXエンジンの応答時間を確認してください。"),
        _ => new(
            ErrorCodes.Api.ApiRequestFailed,
            "APIリクエストでエラーが発生しました。",
            "ネットワーク接続とVOICEVOXエンジンの状態を確認してください。"),
    };

    /// <summary>
    /// 例外からエラー文脈 (Code, UserMessage, Solution) を取得します
    /// </summary>
    public static ErrorContext FromException(Exception exception) => exception switch
    {
        UnauthorizedAccessException => new(
            ErrorCodes.General.PermissionDenied,
            "アクセス権限がありません。",
            "管理者権限で実行するか、必要な権限を確認してください。"),
        IOException => new(
            ErrorCodes.Cache.CacheReadError,
            "ファイルまたはディレクトリの操作に失敗しました。",
            "ファイルの存在とアクセス権限を確認してください。"),
        ArgumentException => new(
            ErrorCodes.General.InvalidArguments,
            "無効な引数が指定されました。",
            "コマンドライン引数を確認し、--helpオプションで使用方法を確認してください。"),
        InvalidOperationException => new(
            ErrorCodes.General.InvalidArguments,
            "操作に必要な前提条件が満たされていません。",
            "アプリケーションの設定と前提条件を確認してください。"),
        TaskCanceledException => new(
            ErrorCodes.Api.ApiTimeout,
            "操作がタイムアウトしました。",
            "ネットワーク接続とVOICEVOXエンジンの応答時間を確認してください。"),
        OperationCanceledException => new(
            ErrorCodes.General.OperationCancelled,
            "操作がキャンセルされました。",
            "操作を再実行してください。"),
        TimeoutException => new(
            ErrorCodes.General.TimeoutError,
            "操作がタイムアウトしました。",
            "ネットワーク接続とVOICEVOXエンジンの応答時間を確認してください。"),
        _ => new(
            ErrorCodes.General.UnknownError,
            "予期しないエラーが発生しました。",
            "アプリケーションを再起動し、問題が続く場合はログを確認してください。"),
    };

    public static string GetErrorCodeFromHttpStatus(HttpStatusCode statusCode) => FromHttpStatus(statusCode).Code;
    public static string GetUserFriendlyMessageFromHttpStatus(HttpStatusCode statusCode) => FromHttpStatus(statusCode).UserMessage;
    public static string GetSuggestedSolutionFromHttpStatus(HttpStatusCode statusCode) => FromHttpStatus(statusCode).Solution;

    public static string GetErrorCodeFromException(Exception exception) => FromException(exception).Code;
    public static string GetUserFriendlyMessageFromException(Exception exception) => FromException(exception).UserMessage;
    public static string GetSuggestedSolutionFromException(Exception exception) => FromException(exception).Solution;

    /// <summary>
    /// エラーコードから終了コードを取得します
    /// </summary>
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

    /// <summary>
    /// エラーの詳細情報をログに記録します
    /// </summary>
    public static void LogErrorDetails(ILogger logger, Exception exception, string operation, string? context = null)
    {
        var ctx = FromException(exception);
        logger.LogError(exception,
            "エラーが発生しました - 操作: {Operation}, エラーコード: {ErrorCode}, コンテキスト: {Context}",
            operation, ctx.Code, context ?? "なし");

        logger.LogInformation("ユーザーメッセージ: {UserMessage}", ctx.UserMessage);
        logger.LogInformation("推奨される解決策: {SuggestedSolution}", ctx.Solution);
    }

    /// <summary>
    /// 例外を処理して、エラーメッセージを出力し、終了コードを返します
    /// </summary>
    public static int HandleExceptionAndGetExitCode(Exception ex, ILogger? logger = null, string? operation = null, string? context = null)
    {
        var ctx = FromException(ex);

        if (logger != null && !String.IsNullOrEmpty(operation))
        {
            logger.LogError(ex, "エラーが発生しました - 操作: {Operation}, エラーコード: {ErrorCode}, コンテキスト: {Context}",
                operation, ctx.Code, context ?? "なし");
        }

        ConsoleHelper.WriteError($"Error: {ctx.UserMessage} - {ex.Message}", logger);
        ConsoleHelper.WriteLine($"解決策: {ctx.Solution}", logger);

        return GetExitCodeFromErrorCode(ctx.Code);
    }
}
