using System.Net;
using Microsoft.Extensions.Logging;
using VoicevoxRunCached.Exceptions;

namespace VoicevoxRunCached.Services;

/// <summary>
/// エラーハンドリングを統一化するためのユーティリティクラス
/// </summary>
public static class ErrorHandlingUtility
{
    /// <summary>
    /// HTTPステータスコードから適切なエラーコードを取得します
    /// </summary>
    public static string GetErrorCodeFromHttpStatus(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.BadRequest => ErrorCodes.Api.API_REQUEST_FAILED,
            HttpStatusCode.Unauthorized => ErrorCodes.Api.API_AUTHENTICATION_ERROR,
            HttpStatusCode.Forbidden => ErrorCodes.General.PERMISSION_DENIED,
            HttpStatusCode.NotFound => ErrorCodes.General.RESOURCE_NOT_FOUND,
            HttpStatusCode.TooManyRequests => ErrorCodes.Api.API_RATE_LIMIT_EXCEEDED,
            HttpStatusCode.InternalServerError => ErrorCodes.Engine.ENGINE_PROCESS_ERROR,
            HttpStatusCode.ServiceUnavailable => ErrorCodes.Engine.ENGINE_NOT_AVAILABLE,
            HttpStatusCode.GatewayTimeout => ErrorCodes.Api.API_TIMEOUT,
            HttpStatusCode.RequestTimeout => ErrorCodes.Api.API_TIMEOUT,
            _ => ErrorCodes.Api.API_REQUEST_FAILED
        };
    }

    /// <summary>
    /// HTTPステータスコードからユーザーフレンドリーなエラーメッセージを取得します
    /// </summary>
    public static string GetUserFriendlyMessageFromHttpStatus(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.BadRequest => "リクエストが無効です。パラメータを確認してください。",
            HttpStatusCode.Unauthorized => "認証に失敗しました。APIキーまたは権限を確認してください。",
            HttpStatusCode.Forbidden => "アクセスが拒否されました。権限を確認してください。",
            HttpStatusCode.NotFound => "リソースが見つかりません。APIバージョンとエンドポイントを確認してください。",
            HttpStatusCode.TooManyRequests => "レート制限を超過しました。しばらく待ってから再試行してください。",
            HttpStatusCode.InternalServerError => "サーバーで内部エラーが発生しました。しばらく待ってから再試行してください。",
            HttpStatusCode.ServiceUnavailable => "サービスが一時的に利用できません。しばらく待ってから再試行してください。",
            HttpStatusCode.GatewayTimeout => "リクエストがタイムアウトしました。しばらく待ってから再試行してください。",
            HttpStatusCode.RequestTimeout => "リクエストがタイムアウトしました。しばらく待ってから再試行してください。",
            _ => "APIリクエストでエラーが発生しました。"
        };
    }

    /// <summary>
    /// HTTPステータスコードから推奨される解決策を取得します
    /// </summary>
    public static string GetSuggestedSolutionFromHttpStatus(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.BadRequest => "リクエストパラメータの形式と値を確認してください。",
            HttpStatusCode.Unauthorized => "APIキーが正しく設定されているか確認してください。",
            HttpStatusCode.Forbidden => "必要な権限があるか確認してください。",
            HttpStatusCode.NotFound => "APIエンドポイントのURLが正しいか確認してください。",
            HttpStatusCode.TooManyRequests => "リクエスト頻度を下げてください。",
            HttpStatusCode.InternalServerError => "VOICEVOXエンジンを再起動してください。",
            HttpStatusCode.ServiceUnavailable => "VOICEVOXエンジンが起動しているか確認してください。",
            HttpStatusCode.GatewayTimeout => "ネットワーク接続とVOICEVOXエンジンの応答時間を確認してください。",
            HttpStatusCode.RequestTimeout => "ネットワーク接続とVOICEVOXエンジンの応答時間を確認してください。",
            _ => "ネットワーク接続とVOICEVOXエンジンの状態を確認してください。"
        };
    }

    /// <summary>
    /// 例外から適切なエラーコードを取得します
    /// </summary>
    public static string GetErrorCodeFromException(Exception exception)
    {
        return exception switch
        {
            UnauthorizedAccessException => ErrorCodes.General.PERMISSION_DENIED,
            IOException => ErrorCodes.Cache.CACHE_READ_ERROR,
            ArgumentException => ErrorCodes.General.INVALID_ARGUMENTS,
            InvalidOperationException => ErrorCodes.General.INVALID_ARGUMENTS,
            TaskCanceledException => ErrorCodes.Api.API_TIMEOUT,
            OperationCanceledException => ErrorCodes.General.OPERATION_CANCELLED,
            TimeoutException => ErrorCodes.General.TIMEOUT_ERROR,
            _ => ErrorCodes.General.UNKNOWN_ERROR
        };
    }

    /// <summary>
    /// 例外からユーザーフレンドリーなエラーメッセージを取得します
    /// </summary>
    public static string GetUserFriendlyMessageFromException(Exception exception)
    {
        return exception switch
        {
            UnauthorizedAccessException => "アクセス権限がありません。",
            IOException => "ファイルまたはディレクトリの操作に失敗しました。",
            ArgumentException => "無効な引数が指定されました。",
            InvalidOperationException => "操作に必要な前提条件が満たされていません。",
            TaskCanceledException => "操作がタイムアウトしました。",
            OperationCanceledException => "操作がキャンセルされました。",
            TimeoutException => "操作がタイムアウトしました。",
            _ => "予期しないエラーが発生しました。"
        };
    }

    /// <summary>
    /// 例外から推奨される解決策を取得します
    /// </summary>
    public static string GetSuggestedSolutionFromException(Exception exception)
    {
        return exception switch
        {
            UnauthorizedAccessException => "管理者権限で実行するか、必要な権限を確認してください。",
            IOException => "ファイルの存在とアクセス権限を確認してください。",
            ArgumentException => "コマンドライン引数を確認し、--helpオプションで使用方法を確認してください。",
            InvalidOperationException => "アプリケーションの設定と前提条件を確認してください。",
            TaskCanceledException => "ネットワーク接続とVOICEVOXエンジンの応答時間を確認してください。",
            OperationCanceledException => "操作を再実行してください。",
            TimeoutException => "ネットワーク接続とVOICEVOXエンジンの応答時間を確認してください。",
            _ => "アプリケーションを再起動し、問題が続く場合はログを確認してください。"
        };
    }

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
            "General" => 1,
            _ => 1
        };
    }

    /// <summary>
    /// エラーの詳細情報をログに記録します
    /// </summary>
    public static void LogErrorDetails(ILogger logger, Exception exception, string operation, string? context = null)
    {
        var errorCode = GetErrorCodeFromException(exception);
        var userMessage = GetUserFriendlyMessageFromException(exception);
        var suggestedSolution = GetSuggestedSolutionFromException(exception);

        logger.LogError(exception,
            "エラーが発生しました - 操作: {Operation}, エラーコード: {ErrorCode}, コンテキスト: {Context}",
            operation, errorCode, context ?? "なし");

        logger.LogInformation("ユーザーメッセージ: {UserMessage}", userMessage);
        logger.LogInformation("推奨される解決策: {SuggestedSolution}", suggestedSolution);
    }
}
